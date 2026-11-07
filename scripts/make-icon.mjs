import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { deflateSync } from 'node:zlib';

const SIZE = 512;
const GROUND = [15, 20, 24];
const LINES = [
  { color: [92, 200, 230], from: [0.16, 0.2], to: [0.5, 0.84], width: 0.085 },
  { color: [92, 200, 230], from: [0.84, 0.2], to: [0.5, 0.84], width: 0.085 },
  { color: [232, 162, 59], from: [0.34, 0.2], to: [0.5, 0.56], width: 0.05 },
  { color: [229, 114, 138], from: [0.66, 0.2], to: [0.5, 0.56], width: 0.05 },
];
const radius = SIZE * 0.2;

const distance = (x, y, a, b) => {
  const dx = b[0] - a[0];
  const dy = b[1] - a[1];
  const t = Math.max(0, Math.min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(x - a[0] - t * dx, y - a[1] - t * dy);
};

const inRounded = (x, y) => {
  const cx = x < radius ? radius : x > SIZE - radius ? SIZE - radius : x;
  const cy = y < radius ? radius : y > SIZE - radius ? SIZE - radius : y;
  return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
};

const rows = Buffer.alloc((SIZE * 4 + 1) * SIZE);
const OFFSETS = [[0.25, 0.25], [0.75, 0.25], [0.25, 0.75], [0.75, 0.75]];
for (let y = 0; y < SIZE; y += 1) {
  const start = y * (SIZE * 4 + 1);
  for (let x = 0; x < SIZE; x += 1) {
    let r = 0;
    let g = 0;
    let b = 0;
    let hits = 0;
    for (const [sx, sy] of OFFSETS) {
      const px = x + sx;
      const py = y + sy;
      if (!inRounded(px, py)) continue;
      hits += 1;
      let color = GROUND;
      for (const line of LINES) {
        if (distance(px / SIZE, py / SIZE, line.from, line.to) <= line.width / 2) color = line.color;
      }
      r += color[0];
      g += color[1];
      b += color[2];
    }
    const at = start + 1 + x * 4;
    if (hits > 0) {
      rows[at] = Math.round(r / hits);
      rows[at + 1] = Math.round(g / hits);
      rows[at + 2] = Math.round(b / hits);
    }
    rows[at + 3] = Math.round((hits / 4) * 255);
  }
}

const table = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc = (buffer) => {
  let c = 0xffffffff;
  for (const byte of buffer) c = table[(c ^ byte) & 255] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type), data]);
  const check = Buffer.alloc(4);
  check.writeUInt32BE(crc(body));
  return Buffer.concat([length, body, check]);
};
const header = Buffer.alloc(13);
header.writeUInt32BE(SIZE, 0);
header.writeUInt32BE(SIZE, 4);
header[8] = 8;
header[9] = 6;
const png = Buffer.concat([
  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
  chunk('IHDR', header),
  chunk('IDAT', deflateSync(rows, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]);
mkdirSync('build', { recursive: true });
writeFileSync(path.join('build', 'icon.png'), png);
