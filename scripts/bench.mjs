import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream, existsSync, mkdirSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import { hashOf } from '../tests/e2e/server.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const MB = 1024 * 1024;
const args = process.argv.slice(2);
const option = (name) => {
  const at = args.indexOf(name);
  return at >= 0 ? args[at + 1] : undefined;
};
const repeats = Number(option('--repeats') ?? 3);
const externalUrl = option('--url');
const authorized = args.includes('--authorized');
const output = path.join(root, 'bench-results.json');

if (externalUrl && !authorized) {
  process.stderr.write('Benchmark externo consome banda e contata um servidor de terceiros. Repita com --authorized para confirmar.\n');
  process.exit(2);
}

const published = path.join(root, 'engine', 'publish', 'velocity-engine.exe');
const built = path.join(root, 'engine', 'Velocity.Engine', 'bin', 'Release', 'net8.0-windows10.0.17763.0', 'win-x64', 'velocity-engine.exe');
const engineExe = existsSync(published) ? published : built;
const aria2Available = spawnSync('aria2c', ['--version'], { stdio: 'ignore' }).status === 0;

function sha256(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    createReadStream(file).on('data', (chunk) => hash.update(chunk)).on('end', () => resolve(hash.digest('hex'))).on('error', reject);
  });
}

async function conventional(url, folder) {
  const target = path.join(folder, 'conv.bin');
  const started = performance.now();
  const reply = await fetch(url);
  if (!reply.ok) throw new Error(`HTTP ${reply.status}`);
  await pipeline(Readable.fromWeb(reply.body), createWriteStream(target));
  return { seconds: (performance.now() - started) / 1000, file: target, connections: 1 };
}

function aria2(url, folder) {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    const child = spawn('aria2c', ['-x16', '-s16', '-k1M', '--file-allocation=none', '--console-log-level=error', '--summary-interval=0', '--allow-overwrite=true', '--auto-file-renaming=false', '-d', folder, '-o', 'aria.bin', url], { stdio: 'ignore', windowsHide: true });
    child.on('error', reject);
    child.on('exit', (code) => {
      if (code === 0) resolve({ seconds: (performance.now() - started) / 1000, file: path.join(folder, 'aria.bin'), connections: 16 });
      else reject(new Error(`aria2c saiu com código ${code}`));
    });
  });
}

function velocity(url, folder, mode = 'auto') {
  return new Promise((resolve, reject) => {
    const data = path.join(folder, 'engine-data');
    const child = spawn(engineExe, ['--data', data], { stdio: 'pipe', windowsHide: true });
    let buffer = '';
    let started = 0;
    let maxConnections = 0;
    let settled = false;
    let downloadEnd = 0;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      child.stdin.write('{"id":0,"cmd":"shutdown"}\n');
      const killer = setTimeout(() => child.kill(), 3000);
      child.once('exit', () => {
        clearTimeout(killer);
        if (error) reject(error);
        else resolve(value);
      });
    };
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => {
      buffer += chunk;
      let index = buffer.indexOf('\n');
      while (index >= 0) {
        const line = buffer.slice(0, index);
        buffer = buffer.slice(index + 1);
        index = buffer.indexOf('\n');
        const message = JSON.parse(line);
        if (message.id === 1) {
          started = performance.now();
          child.stdin.write(`${JSON.stringify({ id: 2, cmd: 'add', args: { urls: [url], folder } })}\n`);
        }
        if (message.id === 2 && !message.ok) finish(new Error(message.error.message));
        if (message.event === 'tick') {
          for (const item of message.data.items) {
            maxConnections = Math.max(maxConnections, item.connections);
            if (!downloadEnd && (item.phase === 'verifying' || item.phase === 'finalizing')) downloadEnd = performance.now();
          }
        }
        if (message.event === 'item') {
          const item = message.data;
          if (item.status === 'done') {
            const seconds = (performance.now() - started) / 1000;
            const peak = spawnSync('powershell', ['-NoProfile', '-Command', `(Get-Process -Id ${child.pid}).PeakWorkingSet64`], { encoding: 'utf8', windowsHide: true });
            finish(null, { seconds, peakMiB: Math.round(Number(peak.stdout.trim()) / MB), transfer: downloadEnd ? (downloadEnd - started) / 1000 : null, file: item.finalPath, connections: maxConnections });
          }
          if (item.status === 'failed' || item.status === 'needs-link' || item.status === 'needs-auth') finish(new Error(`${item.status}: ${item.errorMessage}`));
        }
      }
    });
    child.on('error', reject);
    child.stdin.write(`${JSON.stringify({ id: 1, cmd: 'settings.set', args: { mode } })}\n`);
  });
}

async function startServerProcess() {
  const child = spawn(process.execPath, [path.join(root, 'tests', 'e2e', 'server.mjs'), '0'], { stdio: ['ignore', 'pipe', 'inherit'] });
  const port = await new Promise((resolve) => child.stdout.once('data', (chunk) => resolve(Number(String(chunk).trim()))));
  return { port, stop: () => child.kill() };
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

function line(text) {
  process.stdout.write(`${text}\n`);
}

const scenarios = [
  { id: 'por-conexao', title: 'Servidor limita cada conexão a 10 MiB/s', size: 120 * MB, query: `rate=${10 * MB}`, path: 'a.bin' },
  { id: 'compartilhado', title: 'Limite total de 30 MiB/s, igual a um gargalo da sua internet', size: 90 * MB, query: `shared=${30 * MB}`, path: 'b.bin' },
  { id: 'sem-limite', title: 'Sem limite, só loopback e disco: mede o custo do próprio motor', size: 400 * MB, query: '', path: 'c.bin' },
  { id: 'erro-429', title: 'Servidor responde 429 acima de 2 conexões, limite de 10 MiB/s', size: 40 * MB, query: `rate=${10 * MB}`, path: 'limit429.bin' },
];

const clients = [
  { id: 'convencional', run: conventional },
  { id: 'velocity', run: (url, folder) => velocity(url, folder) },
  { id: 'velocity-turbo', run: (url, folder) => velocity(url, folder, 'turbo') },
  ...(aria2Available ? [{ id: 'aria2', run: aria2 }] : []),
];

const report = { at: new Date().toISOString(), engine: path.basename(path.dirname(engineExe)), cpu: os.cpus()[0]?.model, repeats, scenarios: [] };
const base = mkdtempSync(path.join(option('--dir') ?? os.tmpdir(), 'velocity-bench-'));

if (externalUrl) {
  line(`Benchmark externo autorizado: ${new URL(externalUrl).host}`);
  for (const client of clients) {
    const folder = path.join(base, `ext-${client.id}`);
    mkdirSync(folder, { recursive: true });
    try {
      const result = await client.run(externalUrl, folder);
      const bytes = statSync(result.file).size;
      line(`${client.id.padEnd(16)} ${result.seconds.toFixed(2)} s  ${(bytes / MB / result.seconds).toFixed(1)} MiB/s  conexões ${result.connections}`);
    } catch (error) {
      line(`${client.id.padEnd(16)} falhou: ${error.message}`);
    }
    rmSync(folder, { recursive: true, force: true, maxRetries: 3 });
  }
  rmSync(base, { recursive: true, force: true, maxRetries: 3 });
  process.exit(0);
}

const server = await startServerProcess();
for (const scenario of scenarios) {
  line(`\n${scenario.title} (${scenario.size / MB} MiB)`);
  const expected = hashOf(scenario.size);
  const entry = { id: scenario.id, title: scenario.title, sizeMiB: scenario.size / MB, clients: {} };
  for (const client of clients) {
    const runs = [];
    for (let run = 0; run < repeats; run += 1) {
      const folder = path.join(base, `${scenario.id}-${client.id}-${run}`);
      mkdirSync(folder, { recursive: true });
      const url = `http://127.0.0.1:${server.port}/${scenario.path}?size=${scenario.size}${scenario.query ? `&${scenario.query}` : ''}`;
      await fetch(`http://127.0.0.1:${server.port}/__stats`).then((reply) => reply.json());
      try {
        const result = await client.run(url, folder);
        const intact = (await sha256(result.file)) === expected;
        const stats = await fetch(`http://127.0.0.1:${server.port}/__stats`).then((reply) => reply.json());
        runs.push({ seconds: result.seconds, transfer: result.transfer ?? null, peakMiB: result.peakMiB ?? null, intact, maxActive: stats.maxActive, status429: stats.status429 });
      } catch (error) {
        const stats = await fetch(`http://127.0.0.1:${server.port}/__stats`).then((reply) => reply.json());
        runs.push({ failed: error.message, status429: stats.status429 });
      }
      rmSync(folder, { recursive: true, force: true, maxRetries: 3 });
    }
    const ok = runs.filter((run) => !run.failed);
    const seconds = ok.length ? median(ok.map((run) => run.seconds)) : null;
    const transfers = ok.map((run) => run.transfer).filter((value) => value !== null);
    entry.clients[client.id] = {
      runs,
      medianSeconds: seconds,
      medianMiBs: seconds ? scenario.size / MB / seconds : null,
      medianTransferSeconds: transfers.length ? median(transfers) : null,
      peakMemoryMiB: ok.some((run) => run.peakMiB) ? Math.max(...ok.map((run) => run.peakMiB ?? 0)) : null,
      intact: ok.length > 0 && ok.every((run) => run.intact),
      failures: runs.length - ok.length,
    };
    const shown = seconds
      ? `${seconds.toFixed(2).padStart(7)} s  ${(scenario.size / MB / seconds).toFixed(1).padStart(7)} MiB/s  conexões no servidor ${median(ok.map((run) => run.maxActive))}  429 ${Math.max(...ok.map((run) => run.status429))}  ${entry.clients[client.id].intact ? 'íntegro' : 'CORROMPIDO'}`
      : `falhou em todas: ${runs[0].failed}`;
    line(`  ${client.id.padEnd(16)}${shown}${transfers.length ? `  (transferência ${median(transfers).toFixed(2)} s, memória de pico ${Math.max(...ok.map((run) => run.peakMiB ?? 0))} MiB)` : ''}${ok.length && ok.length < runs.length ? ` (${runs.length - ok.length} falhas)` : ''}`);
  }
  report.scenarios.push(entry);
}
server.stop();
rmSync(base, { recursive: true, force: true, maxRetries: 3 });
writeFileSync(output, JSON.stringify(report, null, 2));
line(`\nResultados em ${output}`);
