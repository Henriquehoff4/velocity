import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const target = path.join('resources', 'tools');
mkdirSync(target, { recursive: true });
const TOOLS = [
  { name: 'aria2c', license: 'GPL-2.0-or-later', source: 'https://github.com/aria2/aria2', version: '--version' },
  { name: 'yt-dlp', license: 'Unlicense', source: 'https://github.com/yt-dlp/yt-dlp', version: '--version' },
];
const manifest = [];

function locate(name) {
  try {
    return execFileSync('where.exe', [`${name}.exe`], { encoding: 'utf8' }).split(/\r?\n/).map((line) => line.trim()).find((line) => line && existsSync(line));
  } catch {
    return undefined;
  }
}

for (const tool of TOOLS) {
  const found = locate(tool.name);
  if (!found) {
    console.log(`AVISO ${tool.name} não encontrado; o instalador sairá sem ele.`);
    continue;
  }
  const file = path.join(target, `${tool.name}.exe`);
  copyFileSync(found, file);
  const version = execFileSync(file, [tool.version], { encoding: 'utf8' }).split(/\r?\n/)[0];
  const sha256 = createHash('sha256').update(readFileSync(file)).digest('hex');
  manifest.push({ name: tool.name, version, sha256, bytes: statSync(file).size, license: tool.license, source: tool.source });
  console.log(`${tool.name}: ${version} ${sha256.slice(0, 16)}`);
}

writeFileSync(path.join(target, 'ferramentas.json'), `${JSON.stringify(manifest, null, 2)}\n`);
writeFileSync(path.join(target, 'LEIA-ME.txt'), [
  'Ferramentas de terceiros incluídas no Velocity:',
  ...manifest.map((tool) => `${tool.name} ${tool.version}, licença ${tool.license}, código-fonte em ${tool.source}`),
  '',
].join('\r\n'));
