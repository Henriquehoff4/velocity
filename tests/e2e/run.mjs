import { createHash } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { _electron as electron } from 'playwright-core';
import { chunkAt, hashOf, startServer } from './server.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const shots = path.join(root, 'tests', 'e2e', 'out');
mkdirSync(shots, { recursive: true });
const STEP_LIMIT = 30000;
const SUITE_LIMIT = 120000;
const started = Date.now();
const base = mkdtempSync(path.join(os.tmpdir(), 'velocity-e2e-'));
const folder = path.join(base, 'downloads');
const userData = path.join(base, 'userdata');
mkdirSync(folder);
const results = [];
const problems = [];
const MB = 1024 * 1024;

const server = await startServer(0, '0.0.0.0');
const origin = `http://127.0.0.1:${server.port}`;
const electronPath = path.join(root, 'node_modules', 'electron', 'dist', 'electron.exe');

let app;
let page;

async function launch() {
  app = await electron.launch({
    executablePath: electronPath,
    args: [root],
    env: {
      ...process.env,
      VELOCITY_TEST: '1',
      VELOCITY_USER_DATA: userData,
      VELOCITY_TEST_FOLDER: folder,
      VELOCITY_TEST_LINKS: '127.0.0.1,127.0.0.2',
    },
    timeout: 25000,
  });
  page = await app.firstWindow();
  page.setDefaultTimeout(12000);
  page.on('console', (message) => {
    if (message.type() === 'error') problems.push(`console: ${message.text()}`);
  });
  page.on('pageerror', (error) => problems.push(`pageerror: ${error.message}`));
  await page.waitForSelector('.shell', { timeout: 15000 });
}

async function step(name, action) {
  if (Date.now() - started > SUITE_LIMIT) {
    results.push({ name, ok: false, ms: 0, error: 'suite passou do limite' });
    process.stdout.write(`FAIL ${name}: suite passou de ${SUITE_LIMIT / 1000} s\n`);
    return;
  }
  const at = Date.now();
  let timer;
  try {
    await Promise.race([action(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`passou de ${STEP_LIMIT / 1000} s`)), STEP_LIMIT); })]);
    results.push({ name, ok: true, ms: Date.now() - at });
    process.stdout.write(`ok   ${name} (${Date.now() - at} ms)\n`);
  } catch (error) {
    results.push({ name, ok: false, ms: Date.now() - at, error: error.message });
    process.stdout.write(`FAIL ${name}: ${error.message}\n`);
    await page?.screenshot({ path: path.join(shots, `falha-${results.length}.png`) }).catch(() => undefined);
  } finally {
    clearTimeout(timer);
  }
}

function check(condition, message) {
  if (!condition) throw new Error(message);
}

function sha256(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    createReadStream(file).on('data', (chunk) => hash.update(chunk)).on('end', () => resolve(hash.digest('hex'))).on('error', reject);
  });
}

const call = (command, args) => page.evaluate(([c, a]) => window.velocity.call(c, a), [command, args]);
const items = () => call('list');
const find = async (name) => (await items()).find((item) => item.name === name);

async function waitItem(name, predicate, timeout = 25000) {
  const limit = Date.now() + timeout;
  let last;
  while (Date.now() < limit) {
    last = await find(name);
    if (last && predicate(last)) return last;
    await page.waitForTimeout(100);
  }
  throw new Error(`esperando ${name}, último estado: ${last?.status ?? 'nenhum'} ${last?.errorMessage ?? ''}`);
}

function bencode(value) {
  if (typeof value === 'number') return Buffer.from(`i${value}e`);
  if (Buffer.isBuffer(value)) return Buffer.concat([Buffer.from(`${value.length}:`), value]);
  if (typeof value === 'string') return bencode(Buffer.from(value));
  if (Array.isArray(value)) return Buffer.concat([Buffer.from('l'), ...value.map(bencode), Buffer.from('e')]);
  const keys = Object.keys(value).sort();
  return Buffer.concat([Buffer.from('d'), ...keys.flatMap((key) => [bencode(key), bencode(value[key])]), Buffer.from('e')]);
}

function makeTorrent(file, name, size, seedBase) {
  const pieceLength = 256 * 1024;
  const pieces = [];
  for (let offset = 0; offset < size; offset += pieceLength) pieces.push(createHash('sha1').update(chunkAt(offset, Math.min(pieceLength, size - offset))).digest());
  writeFileSync(file, bencode({ info: { length: size, name, 'piece length': pieceLength, pieces: Buffer.concat(pieces), private: 1 }, 'url-list': seedBase }));
}

async function addByUi(url) {
  await page.fill('#links', url);
  await page.getByRole('button', { name: /^Baixar/ }).first().click();
}

async function nav(label) {
  await page.getByRole('navigation').getByRole('button', { name: label }).click();
}

async function overflowCheck(label) {
  const overflow = await page.evaluate(() => {
    const doc = document.documentElement;
    const bad = [];
    if (doc.scrollWidth > doc.clientWidth) bad.push('documento');
    for (const selector of ['.sheet', '.linkbar', '.detail', '.queue', '.page', '.top']) {
      const element = document.querySelector(selector);
      if (element && element.scrollWidth > element.clientWidth + 1) bad.push(selector);
    }
    return bad;
  });
  check(overflow.length === 0, `transbordo em ${label}: ${overflow.join(', ')}`);
}

await launch();

await step('abre sem downloads e mostra estado vazio', async () => {
  await page.waitForSelector('text=Nenhum download na fila');
  await page.screenshot({ path: path.join(shots, '01-vazio.png') });
  await overflowCheck('vazio');
});

await step('troca a pasta de destino pela interface', async () => {
  await nav('Ajustes');
  await page.getByRole('button', { name: 'Trocar pasta' }).click();
  await page.waitForSelector(`.path:has-text("downloads")`);
  await page.screenshot({ path: path.join(shots, '02-ajustes.png') });
  await overflowCheck('ajustes');
  await nav('Downloads');
});

await step('identifica arquivo antes de baixar e confere o hash', async () => {
  const size = 8 * MB;
  await page.fill('#links', `${origin}/pequeno.bin?size=${size}`);
  await page.waitForSelector('.previews li:has-text("pequeno.bin")');
  const preview = await page.textContent('.previews');
  check(preview.includes('8,0 MB'), `tamanho não aparece: ${preview}`);
  check(preview.includes('127.0.0.1'), `servidor não aparece: ${preview}`);
  check(preview.includes('retoma'), `retomada não aparece: ${preview}`);
  await page.screenshot({ path: path.join(shots, '03-previa.png') });
  await page.getByRole('button', { name: /^Baixar/ }).first().click();
  const done = await waitItem('pequeno.bin', (item) => item.status === 'done');
  check(done.verified, 'não marcou conferência');
  check((await sha256(done.finalPath)) === hashOf(size), 'hash diferente');
  check(!existsSync(`${done.finalPath}.vdl`), 'journal ficou para trás');
});

await step('download lento mostra velocidade, conexões, mapa e pausa de verdade', async () => {
  const size = 120 * MB;
  await addByUi(`${origin}/grande.bin?size=${size}&rate=${6 * MB}`);
  await waitItem('grande.bin', (item) => item.status === 'running' && item.connections >= 2 && item.speed > 0);
  await page.getByRole('button', { name: /grande\.bin/ }).first().click();
  await page.waitForSelector('.fragment-map');
  await page.waitForTimeout(1500);
  const kpis = await page.textContent('.kpis');
  for (const word of ['Agora', 'Média', 'Máxima', 'Baixado', 'Falta', 'Conexões', 'Fragmentos']) check(kpis.includes(word), `sem ${word}`);
  const pixels = await page.evaluate(() => {
    const canvas = document.querySelector('.fragment-map');
    const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    const colors = new Set();
    for (let i = 0; i < data.length; i += 4 * 97) colors.add(`${data[i]},${data[i + 1]},${data[i + 2]}`);
    return colors.size;
  });
  check(pixels >= 3, `mapa sem cores suficientes: ${pixels}`);
  await page.screenshot({ path: path.join(shots, '04-baixando.png') });
  await overflowCheck('baixando');
  await page.locator('.detail').getByRole('button', { name: 'Pausar' }).click();
  const paused = await waitItem('grande.bin', (item) => item.status === 'paused');
  await page.waitForTimeout(500);
  const later = await find('grande.bin');
  check(later.done === paused.done || later.done - paused.done < MB, 'continuou gravando depois de pausar');
  check(paused.done > 0 && paused.done < size, 'sem progresso parcial');
  await page.screenshot({ path: path.join(shots, '05-pausado.png') });
});

await step('retoma depois de fechar e reabrir o app, sem baixar de novo', async () => {
  const before = await find('grande.bin');
  await call('resume', { id: before.id });
  await waitItem('grande.bin', (item) => item.status === 'running' && item.speed > 0);
  await page.waitForTimeout(800);
  const running = await find('grande.bin');
  await app.close();
  await launch();
  await page.waitForTimeout(300);
  const reopened = await find('grande.bin');
  check(reopened, 'item sumiu');
  check(reopened.done >= before.done, `perdeu progresso: ${reopened.done} < ${before.done}`);
  check(running.done <= reopened.done + 32 * MB, 'perdeu muito mais que o tolerado pelo checkpoint');
  const done = await waitItem('grande.bin', (item) => item.status === 'done');
  check((await sha256(done.finalPath)) === hashOf(120 * MB), 'hash diferente depois de retomar');
});

await step('cancelar exige confirmação e remove o parcial', async () => {
  await addByUi(`${origin}/cancelar.bin?size=${64 * MB}&rate=${1 * MB}`);
  const running = await waitItem('cancelar.bin', (item) => item.status === 'running');
  await page.getByRole('button', { name: /cancelar\.bin/ }).first().click();
  await page.locator('.detail').getByRole('button', { name: 'Cancelar', exact: true }).click();
  await page.getByRole('button', { name: 'Manter' }).click();
  check((await find('cancelar.bin')).status === 'running', 'Manter não manteve');
  await page.locator('.detail').getByRole('button', { name: 'Cancelar', exact: true }).click();
  await page.getByRole('button', { name: 'Cancelar e apagar o parcial' }).click();
  const limit = Date.now() + 8000;
  while ((await find('cancelar.bin')) && Date.now() < limit) await page.waitForTimeout(100);
  check(!(await find('cancelar.bin')), 'item continua na fila');
  check(running.id, 'sem id');
});

await step('fila: reordena por botão e arraste, muda prioridade', async () => {
  await call('settings.set', { maxConcurrent: 1 });
  for (const name of ['a', 'b', 'c']) await call('add', { urls: [`${origin}/${name}.bin?size=${60 * MB}&rate=${2 * MB}`], folder });
  await waitItem('c.bin', (item) => item.status === 'queued' || item.status === 'running');
  const ordered = async () => page.locator('.row-name').allTextContents();
  await page.getByRole('button', { name: /^c\.bin/ }).first().click();
  await page.getByRole('button', { name: 'Subir na fila' }).click();
  await page.waitForFunction(() => {
    const names = [...document.querySelectorAll('.row-name')].map((node) => node.textContent);
    return names.indexOf('c.bin') < names.indexOf('b.bin');
  }, null, { timeout: 5000 });
  await page.locator('.row:has-text("a.bin")').dragTo(page.locator('.row:has-text("c.bin")'));
  await page.waitForFunction(() => {
    const names = [...document.querySelectorAll('.row-name')].map((node) => node.textContent);
    return names.indexOf('a.bin') > names.indexOf('c.bin');
  }, null, { timeout: 5000 }).catch(async () => { throw new Error(`arraste não reordenou: ${(await ordered()).join(', ')}`); });
  await page.getByRole('button', { name: /^b\.bin/ }).first().click();
  await page.getByLabel('Prioridade').selectOption('0');
  await page.waitForFunction(() => document.querySelector('.row .prio')?.textContent === 'Alta', null, { timeout: 5000 });
  await page.screenshot({ path: path.join(shots, '06-fila.png') });
  await call('pauseAll');
  for (const item of await items()) if (item.status !== 'done') await call('cancel', { id: item.id });
  await call('settings.set', { maxConcurrent: 3 });
});

await step('link inválido, 404 e página HTML dizem o que aconteceu', async () => {
  await page.fill('#links', `${origin}/missing`);
  await page.waitForSelector('.previews .bad-text');
  await page.screenshot({ path: path.join(shots, '07-erro-link.png') });
  await page.fill('#links', `${origin}/page`);
  await page.waitForSelector('.html-choice');
  await page.screenshot({ path: path.join(shots, '08-pagina.png') });
  await page.fill('#links', 'isto não é um link');
  check(await page.getByRole('button', { name: /^Baixar/ }).first().isDisabled(), 'botão deveria ficar desligado');
  await page.fill('#links', '');
});

await step('servidor pede login: formulário aparece e o download continua', async () => {
  const size = 4 * MB;
  await addByUi(`${origin}/auth.bin?size=${size}`);
  await waitItem('auth.bin', (item) => item.status === 'needs-auth');
  await page.getByRole('button', { name: /auth\.bin/ }).first().click();
  await page.fill('#auth-user', 'ana');
  await page.fill('#auth-pass', 'segredo');
  await page.screenshot({ path: path.join(shots, '09-login.png') });
  await page.getByRole('button', { name: 'Entrar e continuar' }).click();
  const done = await waitItem('auth.bin', (item) => item.status === 'done');
  check((await sha256(done.finalPath)) === hashOf(size), 'hash diferente');
  check(!readFileSync(path.join(userData, 'engine', 'queue.json'), 'utf8').includes('segredo'), 'senha em texto puro na fila');
});

await step('link expira: pede link novo e continua do ponto em que parou', async () => {
  const size = 40 * MB;
  await addByUi(`${origin}/expiring.bin?size=${size}&rate=${4 * MB}`);
  await waitItem('expiring.bin', (item) => item.status === 'running' && item.done > 4 * MB);
  await call('pause', { id: (await find('expiring.bin')).id });
  const paused = await waitItem('expiring.bin', (item) => item.status === 'paused');
  server.state.gone = true;
  await call('resume', { id: paused.id });
  await waitItem('expiring.bin', (item) => item.status === 'needs-link');
  await page.getByRole('button', { name: /expiring\.bin/ }).first().click();
  await page.waitForSelector('#new-link');
  await page.screenshot({ path: path.join(shots, '10-link-novo.png') });
  server.state.gone = false;
  await page.fill('#new-link', `${origin}/expiring.bin?size=${size}&novo=1`);
  await page.getByRole('button', { name: 'Continuar com este link' }).click();
  const done = await waitItem('expiring.bin', (item) => item.status === 'done');
  check((await sha256(done.finalPath)) === hashOf(size), 'hash diferente');
});

await step('colar e sugestão da área de transferência, sem iniciar sozinho', async () => {
  const before = await app.evaluate(({ clipboard }) => clipboard.readText());
  const link = `${origin}/colado.bin?size=${2 * MB}`;
  try {
    await app.evaluate(({ clipboard }, text) => clipboard.writeText(text), link);
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.send('window:focus', null));
    await page.waitForSelector('.clip');
    check((await page.textContent('.clip')).includes('colado.bin'), 'sugestão sem o link');
    check(!(await find('colado.bin')), 'iniciou sem pedir');
    await page.screenshot({ path: path.join(shots, '15-sugestao.png') });
    await page.getByRole('button', { name: 'Ignorar' }).click();
    await page.getByRole('button', { name: 'Colar' }).click();
    await page.waitForFunction((text) => document.querySelector('#links').value === text, link, { timeout: 5000 });
    await page.fill('#links', '');
  } finally {
    await app.evaluate(({ clipboard }, text) => clipboard.writeText(text), before);
  }
});

await step('checksum informado pela interface é conferido', async () => {
  const size = 3 * MB;
  await page.getByRole('button', { name: 'Mais opções' }).click();
  await page.getByLabel(/^Checksum/).fill(hashOf(size));
  await addByUi(`${origin}/conferido.bin?size=${size}`);
  const done = await waitItem('conferido.bin', (item) => item.status === 'done');
  check(done.checksumState === 'ok', `checksum: ${done.checksumState}`);
  await page.getByRole('button', { name: /conferido\.bin/ }).first().click();
  await page.waitForSelector('text=O checksum do arquivo confere');
  await page.getByRole('button', { name: 'Menos opções' }).click();
});

await step('captura do navegador exige código e confirmação', async () => {
  await nav('Ajustes');
  await page.getByLabel('Receber downloads da extensão').check();
  await page.waitForSelector('.steps');
  const state = await page.evaluate(() => window.velocity.captureState());
  const send = (token, body) => fetch(`http://127.0.0.1:${state.port}/capture`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` }, body: JSON.stringify(body) });
  check((await send('errado', { url: `${origin}/capturado.bin` })).status === 401, 'aceitou código errado');
  check((await send(state.token, { url: `${origin}/capturado.bin?size=${2 * MB}`, name: 'capturado.bin' })).status === 202, 'recusou captura válida');
  await nav('Downloads');
  await page.waitForSelector('.captures li:has-text("capturado.bin")');
  check(!(await find('capturado.bin')), 'iniciou sem confirmação');
  await page.screenshot({ path: path.join(shots, '16-captura.png') });
  await page.locator('.captures').getByRole('button', { name: 'Baixar' }).click();
  await waitItem('capturado.bin', (item) => item.status === 'done');
  await nav('Ajustes');
  await page.getByLabel('Receber downloads da extensão').uncheck();
  await nav('Downloads');
});

await step('torrent real pelo aria2, privado e com fonte local', async () => {
  const size = 16 * MB;
  const torrent = path.join(base, 'local.torrent');
  makeTorrent(torrent, 'local.bin', size, `${origin}/seed/`);
  await call('add', { urls: [], torrentFile: torrent, folder });
  const limit = Date.now() + 22000;
  let item;
  while (Date.now() < limit) {
    item = (await items()).find((entry) => entry.kind === 'torrent');
    if (item && (item.status === 'done' || item.status === 'failed')) break;
    await page.waitForTimeout(150);
  }
  check(item?.status === 'done', `torrent: ${item?.status} ${item?.errorMessage ?? ''}`);
  check((await sha256(path.join(folder, 'local.bin'))) === hashOf(size), 'hash do torrent diferente');
});

await step('vídeo real pelo yt-dlp contra servidor local', async () => {
  const size = 2 * MB;
  await call('add', { urls: [`${origin}/video.mp4?size=${size}`], kind: 'video', folder });
  const limit = Date.now() + 25000;
  let item;
  while (Date.now() < limit) {
    item = (await items()).find((entry) => entry.kind === 'video');
    if (item && (item.status === 'done' || item.status === 'failed')) break;
    await page.waitForTimeout(150);
  }
  check(item?.status === 'done', `vídeo: ${item?.status} ${item?.errorMessage ?? ''}`);
  const saved = readdirSync(folder).find((name) => name.endsWith('.mp4'));
  check(saved, 'arquivo de vídeo ausente');
  check((await sha256(path.join(folder, saved))) === hashOf(size), 'hash do vídeo diferente');
});

await step('tela de redes é honesta e detecta as interfaces de teste', async () => {
  await nav('Redes');
  await page.waitForSelector('table');
  const text = await page.textContent('.page');
  check(text.includes('127.0.0.1') && text.includes('127.0.0.2'), 'interfaces de teste ausentes');
  check(text.includes('api.ipify.org') && text.includes('Só roda quando você pede'), 'aviso de serviço externo ausente');
  check(text.includes('Speedify'), 'explicação de bonding ausente');
  await page.screenshot({ path: path.join(shots, '11-redes.png') });
  await overflowCheck('redes');
});

await step('histórico lista concluídos sem guardar a URL completa', async () => {
  await nav('Histórico');
  await page.waitForSelector('.table');
  const text = await page.textContent('.page');
  check(text.includes('pequeno.bin') && text.includes('Concluído'), 'histórico incompleto');
  check(!text.includes('?size='), 'histórico mostra query string');
  await page.screenshot({ path: path.join(shots, '12-historico.png') });
  await overflowCheck('histórico');
});

await step('modos mudam de verdade e captura liga com instruções', async () => {
  await nav('Ajustes');
  await page.getByRole('radio', { name: 'Avançado' }).check({ force: true });
  await page.waitForFunction(() => !document.querySelector('fieldset.plain').disabled, null, { timeout: 5000 });
  check((await call('settings.get')).mode === 'advanced', 'modo não salvou');
  await page.getByLabel('Máximo de conexões').fill('4');
  await page.getByLabel('Máximo de conexões').blur();
  await page.waitForTimeout(200);
  check((await call('settings.get')).advanced.maxConnections === 4, 'máximo não salvou');
  await page.getByLabel('Receber downloads da extensão').check();
  await page.waitForSelector('.steps');
  await page.screenshot({ path: path.join(shots, '13-ajustes-avancado.png'), fullPage: true });
  await page.getByLabel('Receber downloads da extensão').uncheck();
  await page.getByRole('radio', { name: 'Automático' }).check({ force: true });
});

await step('layout aguenta 1024x640 e 1920x1080', async () => {
  await nav('Downloads');
  await addByUi(`${origin}/um-nome-de-arquivo-bem-comprido-para-ver-se-o-layout-quebra-em-telas-pequenas-versao-final-2026.bin?size=${50 * MB}&rate=${2 * MB}`);
  await page.waitForSelector('.row');
  await page.waitForTimeout(1200);
  const window = await app.browserWindow(page);
  for (const [width, height] of [[1024, 640], [1920, 1080]]) {
    await window.evaluate((win, size) => win.setContentSize(size[0], size[1]), [width, height]);
    await page.waitForTimeout(400);
    await page.screenshot({ path: path.join(shots, `14-${width}x${height}.png`) });
    await overflowCheck(`${width}x${height}`);
  }
  await window.evaluate((win) => win.setContentSize(1360, 860));
  await call('pauseAll');
});

await step('teclado alcança as ações principais', async () => {
  await page.locator('nav button').first().focus();
  const reached = [];
  for (let i = 0; i < 16; i += 1) {
    await page.keyboard.press('Tab');
    reached.push(await page.evaluate(() => document.activeElement?.getAttribute('aria-label') || document.activeElement?.textContent?.slice(0, 24) || document.activeElement?.id || document.activeElement?.tagName));
  }
  const joined = reached.join('|');
  check(joined.includes('links'), `sem foco no campo de links: ${joined}`);
  const focusStyle = await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle);
  check(focusStyle !== 'none', 'sem foco visível');
});

await step('nenhum erro no console durante a sessão', async () => {
  const real = problems.filter((entry) => !entry.includes('Failed to load resource'));
  check(real.length === 0, real.slice(0, 5).join(' | '));
});

await app.close().catch(() => undefined);
server.close();
rmSync(base, { recursive: true, force: true, maxRetries: 3 });
const failed = results.filter((entry) => !entry.ok);
process.stdout.write(`\n${results.length - failed.length}/${results.length} aprovados em ${Math.round((Date.now() - started) / 1000)} s; máximo do servidor: ${server.stats.maxActive} conexões simultâneas\n`);
process.exit(failed.length > 0 ? 1 : 0);
