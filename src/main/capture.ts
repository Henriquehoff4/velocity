import { randomBytes, timingSafeEqual } from 'node:crypto';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { isAllowedHost, isAllowedOrigin, parseCapture, safeDisplay } from '../shared/links';
import type { CaptureRequest } from '../shared/types';

const MAX_BODY = 64 * 1024;
const MAX_PENDING = 50;
const MAX_PER_MINUTE = 60;

export function newToken(): string {
  return randomBytes(24).toString('hex');
}

function sameToken(given: string, expected: string): boolean {
  const a = Buffer.from(given);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

export class CaptureServer {
  private server: Server | null = null;
  private pending = new Map<string, CaptureRequest>();
  private urls = new Map<string, { url: string; referrer: string }>();
  private recent: number[] = [];

  constructor(
    private getToken: () => string,
    private onRequest: (request: CaptureRequest) => void,
  ) {}

  port = 0;

  get running(): boolean {
    return this.server !== null;
  }

  list(): CaptureRequest[] {
    return [...this.pending.values()];
  }

  take(id: string): { url: string; referrer: string } | null {
    const value = this.urls.get(id) ?? null;
    this.pending.delete(id);
    this.urls.delete(id);
    return value;
  }

  async start(preferred: number): Promise<number> {
    if (this.server) return this.port;
    for (const candidate of [preferred, 0]) {
      try {
        this.port = await this.listen(candidate);
        return this.port;
      } catch {
        this.server = null;
      }
    }
    throw new Error('Não foi possível abrir a porta local de captura.');
  }

  private listen(port: number): Promise<number> {
    return new Promise((resolve, reject) => {
      const server = createServer((req, res) => {
        void this.handle(req, res);
      });
      server.on('error', reject);
      server.listen(port, '127.0.0.1', () => {
        this.server = server;
        const address = server.address();
        resolve(typeof address === 'object' && address ? address.port : port);
      });
    });
  }

  stop(): void {
    this.server?.close();
    this.server = null;
  }

  private reply(res: ServerResponse, status: number, body: object, origin?: string): void {
    const headers: Record<string, string> = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' };
    if (origin) headers['Access-Control-Allow-Origin'] = origin;
    res.writeHead(status, headers);
    res.end(JSON.stringify(body));
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const origin = req.headers.origin;
    if (!isAllowedHost(req.headers.host, this.port) || !isAllowedOrigin(origin)) {
      this.reply(res, 403, { ok: false });
      return;
    }
    if (req.method === 'OPTIONS') {
      res.writeHead(204, {
        'Access-Control-Allow-Origin': origin ?? '',
        'Access-Control-Allow-Methods': 'POST',
        'Access-Control-Allow-Headers': 'Authorization, Content-Type',
        'Access-Control-Max-Age': '600',
      });
      res.end();
      return;
    }
    const authorization = req.headers.authorization ?? '';
    const given = authorization.startsWith('Bearer ') ? authorization.slice(7) : '';
    if (!sameToken(given, this.getToken())) {
      this.reply(res, 401, { ok: false }, origin);
      return;
    }
    if (req.method !== 'POST' || req.url !== '/capture' || !(req.headers['content-type'] ?? '').startsWith('application/json')) {
      this.reply(res, 404, { ok: false }, origin);
      return;
    }
    const now = Date.now();
    this.recent = this.recent.filter((at) => now - at < 60000);
    if (this.recent.length >= MAX_PER_MINUTE || this.pending.size >= MAX_PENDING) {
      this.reply(res, 429, { ok: false }, origin);
      return;
    }
    let body: unknown;
    try {
      body = JSON.parse(await readBody(req));
    } catch {
      this.reply(res, 400, { ok: false }, origin);
      return;
    }
    const capture = parseCapture(body);
    if (!capture) {
      this.reply(res, 400, { ok: false }, origin);
      return;
    }
    this.recent.push(now);
    const id = randomBytes(8).toString('hex');
    const display = safeDisplay(capture.url);
    const request: CaptureRequest = { id, url: '', display, name: capture.name || display.split('/').pop() || 'download', referrer: '' };
    this.pending.set(id, request);
    this.urls.set(id, { url: capture.url, referrer: capture.referrer });
    this.onRequest(request);
    this.reply(res, 202, { ok: true }, origin);
  }
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new Error('corpo grande demais'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}
