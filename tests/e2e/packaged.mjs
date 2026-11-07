import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hashOf, startServer } from './server.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const exe = path.join(root, 'release', 'win-unpacked', 'Velocity.exe');
if (!existsSync(exe)) {
  console.error('Rode npm run dist antes.');
  process.exit(1);
}
const shots = path.join(root, 'tests', 'e2e', 'out');
mkdirSync(shots, { recursive: true });
const base = mkdtempSync(path.join(os.tmpdir(), 'velocity-pack-'));
const folder = path.join(base, 'downloads');
const userData = path.join(base, 'userdata');
mkdirSync(folder);
const MB = 1024 * 1024;
const results = [];
const check = (name, ok, detail = '') => {
  results.push(ok);
  console.log(`${ok ? 'OK   ' : 'FALHA'} ${name}${detail ? ` | ${detail}` : ''}`);
};
const guard = setTimeout(() => {
  console.error('Limite de 30 s atingido.');
  process.exit(1);
}, 30000);
const sha256 = (file) => new Promise((resolve, reject) => {
  const hash = createHash('sha256');
  createReadStream(file).on('data', (chunk) => hash.update(chunk)).on('end', () => resolve(hash.digest('hex'))).on('error', reject);
});

const server = await startServer(0, '0.0.0.0');
const origin = `http://127.0.0.1:${server.port}`;
const debugPort = 19000 + Math.floor(Math.random() * 2000);
const app = spawn(exe, [`--user-data-dir=${userData}`, `--remote-debugging-port=${debugPort}`], {
  stdio: 'ignore',
  env: { ...process.env, VELOCITY_TEST: '1', VELOCITY_TEST_LINKS: '127.0.0.9', VELOCITY_USER_DATA: path.join(base, 'ignorar'), VELOCITY_TEST_FOLDER: path.join(base, 'ignorar') },
});
let browser;
for (let attempt = 0; attempt < 60 && !browser; attempt += 1) {
  await new Promise((resolve) => setTimeout(resolve, 250));
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`).catch(() => undefined);
}
if (!browser) {
  console.error('Não conectou ao app empacotado.');
  app.kill();
  process.exit(1);
}
let page = browser.contexts()[0]?.pages()[0];
for (let attempt = 0; attempt < 40 && !page; attempt += 1) {
  await new Promise((resolve) => setTimeout(resolve, 250));
  page = browser.contexts()[0]?.pages()[0];
}
page.setDefaultTimeout(10000);
const problems = [];
page.on('pageerror', (error) => problems.push(error.message));
page.on('console', (message) => {
  if (message.type() === 'error' && !/Failed to load resource|ERR_/.test(message.text())) problems.push(message.text());
});
const call = (command, args = {}) => page.evaluate(([c, a]) => window.velocity.call(c, a), [command, args]);
const waitItem = async (name, test) => {
  const limit = Date.now() + 15000;
  let last;
  while (Date.now() < limit) {
    last = (await call('list')).find((item) => item.name === name);
    if (last && test(last)) return last;
    await page.waitForTimeout(100);
  }
  throw new Error(`${name}: ${last?.status} ${last?.errorMessage ?? ''}`);
};

try {
  await page.waitForSelector('.shell');
  const info = await page.evaluate(() => window.velocity.info());
  check('Roda empacotado e ignora o modo de teste', info.isPackaged && !info.testMode);

  const net = await call('net.list');
  check('Variáveis de teste não alteram as redes reais', !JSON.stringify(net).includes('127.0.0.9'), `${net.links.length} rede(s): ${net.links.map((link) => link.kind).join(', ')}`);

  await call('settings.set', { folder });
  const tools = await call('tools');
  check('aria2 e yt-dlp empacotados são encontrados', tools.filter((tool) => tool.bundled && tool.available).length >= 2, tools.map((tool) => `${tool.name}=${tool.available}/${tool.bundled}`).join(' '));

  const inspected = await call('inspect', { urls: [`${origin}/pacote.bin?size=${40 * MB}`] });
  check('Identifica o arquivo com o motor publicado e reduzido', inspected[0].ok && inspected[0].ranges && inspected[0].size === 40 * MB);

  await call('add', { urls: [`${origin}/pacote.bin?size=${40 * MB}&rate=${8 * MB}`], folder });
  const running = await waitItem('pacote.bin', (item) => item.status === 'running' && item.done > 4 * MB);
  check('Baixa com várias conexões', running.target >= 2, `alvo ${running.target}, conexões ${running.connections}`);
  await call('pause', { id: running.id });
  await waitItem('pacote.bin', (item) => item.status === 'paused');
  await call('resume', { id: running.id });
  const done = await waitItem('pacote.bin', (item) => item.status === 'done');
  check('Pausa, continua e entrega o arquivo íntegro', (await sha256(done.finalPath)) === hashOf(40 * MB) && done.verified);
  await page.screenshot({ path: path.join(shots, 'pacote.png') });

  await call('setAuth', { id: 'inexistente', user: 'x', password: 'y' }).catch(() => undefined);
  const history = await call('history.list', { limit: 10 });
  check('Histórico funciona no motor reduzido', history.some((entry) => entry.name === 'pacote.bin' && entry.outcome === 'done'));

  const blocked = await page.evaluate(() => window.velocity.call('shutdown').then(() => false, () => true));
  check('Interface não consegue chamar comandos fora da lista', blocked);
  check('Sem erros no console', problems.length === 0, problems.slice(0, 3).join(' | '));
} catch (error) {
  check('Fluxo empacotado', false, error.message);
}

await browser.close().catch(() => undefined);
await new Promise((resolve) => {
  app.once('exit', resolve);
  spawn('taskkill', ['/pid', String(app.pid), '/t', '/f'], { stdio: 'ignore' });
});
await new Promise((resolve) => setTimeout(resolve, 500));
server.close();
clearTimeout(guard);
rmSync(base, { recursive: true, force: true, maxRetries: 3 });
const failed = results.filter((ok) => !ok).length;
console.log(`\n${results.length - failed}/${results.length} aprovados`);
process.exit(failed ? 1 : 0);
