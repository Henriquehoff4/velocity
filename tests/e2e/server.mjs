import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { pathToFileURL } from 'node:url';

const BLOCK = 1024 * 1024;
const block = Buffer.alloc(BLOCK);
let seed = 0x9e3779b9;
for (let i = 0; i < BLOCK; i += 4) {
  seed ^= seed << 13;
  seed ^= seed >>> 17;
  seed ^= seed << 5;
  block.writeInt32LE(seed | 0, i);
}

export function chunkAt(offset, length) {
  const output = Buffer.allocUnsafe(length);
  let written = 0;
  while (written < length) {
    const position = offset + written;
    const index = Math.floor(position / BLOCK);
    const inside = position % BLOCK;
    const take = Math.min(length - written, BLOCK - inside);
    block.copy(output, written, inside, inside + take);
    for (let i = 0; i < 8; i += 1) {
      const at = i - inside;
      if (at >= 0 && at < take) output[written + at] = (index >>> (i * 3)) & 0xff;
    }
    written += take;
  }
  return output;
}

export function hashOf(size) {
  const hash = createHash('sha256');
  for (let offset = 0; offset < size; offset += BLOCK) hash.update(chunkAt(offset, Math.min(BLOCK, size - offset)));
  return hash.digest('hex');
}

function parseRange(header, size) {
  const match = /^bytes=(\d*)-(\d*)$/.exec(header ?? '');
  if (!match || (match[1] === '' && match[2] === '')) return null;
  if (match[1] === '') {
    const suffix = Math.min(size, Number(match[2]));
    return [size - suffix, size - 1];
  }
  const start = Number(match[1]);
  const end = match[2] === '' ? size - 1 : Math.min(size - 1, Number(match[2]));
  return start > end ? null : [start, end];
}

class SharedBucket {
  constructor(rate) {
    this.rate = rate;
    this.tokens = 0;
    this.last = performance.now();
  }

  take(wanted) {
    const now = performance.now();
    this.tokens = Math.min(this.rate * 0.1, this.tokens + ((now - this.last) / 1000) * this.rate);
    this.last = now;
    const granted = Math.min(wanted, Math.floor(this.tokens));
    this.tokens -= granted;
    return granted;
  }
}

function send(res, offset, end, options) {
  const { connRate, shared, dropAfter } = options;
  let position = offset;
  let sent = 0;
  let last = performance.now();
  let credit = 0;
  const step = () => {
    if (res.destroyed) return;
    if (position > end) {
      res.end();
      return;
    }
    let budget = 1024 * 1024;
    if (connRate > 0) {
      const now = performance.now();
      credit = Math.min(connRate * 0.1, credit + ((now - last) / 1000) * connRate);
      last = now;
      budget = Math.floor(credit);
    }
    if (shared) budget = Math.min(budget, Math.min(256 * 1024, end - position + 1));
    let length = Math.min(budget, 256 * 1024, end - position + 1);
    if (shared && length > 0) length = shared.take(length);
    if (length <= 0) {
      setTimeout(step, 5);
      return;
    }
    if (connRate > 0) credit -= length;
    const data = chunkAt(position, length);
    position += length;
    sent += length;
    if (dropAfter > 0 && sent >= dropAfter) {
      res.socket?.destroy();
      return;
    }
    if (res.write(data)) setImmediate(step);
    else res.once('drain', step);
  };
  step();
}

export function startServer(port = 0, host = '127.0.0.1') {
  const stats = { requests: 0, maxActive: 0, active: 0, status429: 0 };
  const state = { gone: false, dropOnce: new Set(), failNext429: 0 };
  const shared = new Map();
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://local');
    const size = url.pathname.startsWith('/seed/') ? 16 * 1024 * 1024 : Number(url.searchParams.get('size') ?? 1024 * 1024);
    const connRate = Number(url.searchParams.get('rate') ?? 0);
    const sharedRate = Number(url.searchParams.get('shared') ?? 0);
    stats.requests += 1;
    stats.active += 1;
    stats.maxActive = Math.max(stats.maxActive, stats.active);
    res.on('close', () => {
      stats.active -= 1;
    });
    const path = url.pathname;
    if (path === '/__stats') {
      const snapshot = { requests: stats.requests - 1, maxActive: stats.maxActive, status429: stats.status429 };
      stats.requests = 0;
      stats.maxActive = 0;
      stats.status429 = 0;
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(snapshot));
      return;
    }
    if (path === '/page') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end('<!doctype html><title>Página</title><h1>Página</h1>');
      return;
    }
    if (path === '/missing') {
      res.writeHead(404);
      res.end();
      return;
    }
    if (path === '/auth.bin') {
      const expected = `Basic ${Buffer.from('ana:segredo').toString('base64')}`;
      if (req.headers.authorization !== expected) {
        res.writeHead(401, { 'WWW-Authenticate': 'Basic realm="teste"' });
        res.end();
        return;
      }
    }
    if (path === '/expiring.bin' && state.gone) {
      res.writeHead(403);
      res.end();
      return;
    }
    if (path === '/limit429.bin' && req.headers.range && stats.active > 2) {
      stats.status429 += 1;
      res.writeHead(429, { 'Retry-After': '1' });
      res.end();
      return;
    }
    const etag = `"v-${size}"`;
    const headers = { 'Content-Type': path.endsWith('.mp4') ? 'video/mp4' : 'application/octet-stream', ETag: etag, 'Content-Disposition': `attachment; filename="${url.searchParams.get('name') ?? path.slice(1)}"` };
    const options = { connRate, shared: null, dropAfter: 0 };
    if (sharedRate > 0) {
      const key = `${path}|${sharedRate}`;
      if (!shared.has(key)) shared.set(key, new SharedBucket(sharedRate));
      options.shared = shared.get(key);
    }
    if (path === '/flaky.bin' && !state.dropOnce.has(req.headers.range ?? 'all') && state.dropOnce.size < 3) {
      state.dropOnce.add(req.headers.range ?? 'all');
      options.dropAfter = 512 * 1024;
    }
    if (req.method === 'HEAD') {
      const allow = path !== '/norange.bin';
      res.writeHead(200, { ...headers, 'Content-Length': size, ...(allow ? { 'Accept-Ranges': 'bytes' } : {}) });
      res.end();
      return;
    }
    if (path === '/norange.bin' || !req.headers.range) {
      res.writeHead(200, { ...headers, 'Content-Length': size, ...(path === '/norange.bin' ? {} : { 'Accept-Ranges': 'bytes' }) });
      send(res, 0, size - 1, options);
      return;
    }
    const range = parseRange(req.headers.range, size);
    if (!range) {
      res.writeHead(416, { 'Content-Range': `bytes */${size}` });
      res.end();
      return;
    }
    res.writeHead(206, { ...headers, 'Accept-Ranges': 'bytes', 'Content-Length': range[1] - range[0] + 1, 'Content-Range': `bytes ${range[0]}-${range[1]}/${size}` });
    send(res, range[0], range[1], options);
  });
  return new Promise((resolve, reject) => {
    server.on('error', reject);
    server.listen(port, host, () => {
      resolve({
        port: server.address().port,
        stats,
        state,
        close: () => {
          server.closeAllConnections();
          server.close();
        },
      });
    });
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const running = await startServer(Number(process.argv[2] ?? 0), process.argv[3] ?? '127.0.0.1');
  process.stdout.write(`${running.port}\n`);
}
