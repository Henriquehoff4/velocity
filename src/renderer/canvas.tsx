import { useEffect, useRef, useState, type MouseEvent } from 'react';
import { formatBytes, formatSpeed } from '../shared/format';
import type { Item, MapData } from '../shared/types';
import { SERIES_LENGTH, store, type Series } from './store';

const TRACK = '#232b32';
const DONE = '#4f8a9e';
const INK = '#e6ecef';
const MUTED = '#9aa8b2';
const GRID = '#262f36';
const CURSOR = '#e6ecef';
const ROWS = 6;
const TOP_PAD = 10;

function useCanvasSize(ref: React.RefObject<HTMLCanvasElement | null>): { width: number; height: number; ratio: number } {
  const [size, setSize] = useState({ width: 0, height: 0, ratio: 1 });
  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const observer = new ResizeObserver(() => {
      const ratio = window.devicePixelRatio || 1;
      const rect = canvas.getBoundingClientRect();
      canvas.width = Math.round(rect.width * ratio);
      canvas.height = Math.round(rect.height * ratio);
      setSize({ width: rect.width, height: rect.height, ratio });
    });
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [ref]);
  return size;
}

function drawRange(ctx: CanvasRenderingContext2D, from: number, to: number, size: number, width: number, rowHeight: number, gap: number, color: string): void {
  if (to <= from) return;
  ctx.fillStyle = color;
  const perRow = size / ROWS;
  for (let row = Math.floor(from / perRow); row < ROWS && row * perRow < to; row += 1) {
    const start = Math.max(from, row * perRow) - row * perRow;
    const end = Math.min(to, (row + 1) * perRow) - row * perRow;
    const x = (start / perRow) * width;
    const w = Math.max(1, ((end - start) / perRow) * width);
    ctx.fillRect(x, row * (rowHeight + gap), w, rowHeight);
  }
}

export function FragmentMap({ item, map }: { item: Item; map: MapData | null }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const size = useCanvasSize(ref);
  const [hover, setHover] = useState<{ x: number; y: number; text: string } | null>(null);

  const total = map?.size && map.size > 0 ? map.size : item.size;
  const gap = 3;
  const rowHeight = size.height > 0 ? (size.height - gap * (ROWS - 1)) / ROWS : 0;

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx || size.width === 0) return;
    ctx.setTransform(size.ratio, 0, 0, size.ratio, 0, 0);
    ctx.clearRect(0, 0, size.width, size.height);
    ctx.fillStyle = TRACK;
    for (let row = 0; row < ROWS; row += 1) ctx.fillRect(0, row * (rowHeight + gap), size.width, rowHeight);
    if (total <= 0) return;
    if (map) {
      for (const [start, end] of map.done) drawRange(ctx, start, end, total, size.width, rowHeight, gap, DONE);
      for (const span of map.active) {
        const color = store.linkColor(span.l);
        drawRange(ctx, span.s, span.p, total, size.width, rowHeight, gap, color);
        const perRow = total / ROWS;
        const row = Math.min(ROWS - 1, Math.floor(span.p / perRow));
        const x = ((span.p - row * perRow) / perRow) * size.width;
        ctx.fillStyle = INK;
        ctx.fillRect(Math.max(0, x - 1), row * (rowHeight + gap), 2, rowHeight);
      }
    } else {
      drawRange(ctx, 0, item.status === 'done' ? total : Math.max(0, item.done), total, size.width, rowHeight, gap, DONE);
    }
  }, [map, item.done, item.status, total, size, rowHeight]);

  const onMove = (event: MouseEvent<HTMLCanvasElement>) => {
    if (!map || total <= 0 || size.width === 0) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const row = Math.min(ROWS - 1, Math.floor(y / (rowHeight + gap)));
    const offset = (row + x / size.width) * (total / ROWS);
    const span = map.active.find((entry) => offset >= entry.s && offset < entry.e);
    const name = span ? item.links?.find((link) => link.id === span.l)?.name ?? 'rede' : '';
    const text = span
      ? `Fragmento em ${formatBytes(span.s)}: faltam ${formatBytes(span.e - span.p)}, a ${formatSpeed(span.v)} por ${name}`
      : map.done.some(([start, end]) => offset >= start && offset < end)
        ? `${formatBytes(offset)}: já gravado`
        : `${formatBytes(offset)}: ainda falta`;
    setHover({ x: Math.min(x, size.width - 260), y, text });
  };

  const percent = total > 0 ? Math.round((item.done / total) * 100) : 0;
  const label = `Mapa do arquivo: ${percent}% gravado, ${map?.active.length ?? 0} fragmentos em andamento.`;
  return (
    <div className="map-wrap">
      <canvas ref={ref} className="fragment-map" role="img" aria-label={label} onMouseMove={onMove} onMouseLeave={() => setHover(null)} />
      {hover ? <div className="chart-tip" style={{ left: Math.max(0, hover.x), top: hover.y + 14 }}>{hover.text}</div> : null}
    </div>
  );
}

function windowOf(series: Series | undefined): number {
  return Math.min(SERIES_LENGTH, Math.max(120, series?.filled ?? 0));
}

function niceMax(value: number): number {
  if (value <= 0) return 1024 * 1024;
  const magnitude = 1024 ** Math.floor(Math.log(value) / Math.log(1024));
  const scaled = value / magnitude;
  const steps = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];
  return (steps.find((step) => step >= scaled * 1.1) ?? 1024) * magnitude;
}

export function SpeedChart({ series, label, version }: { series: Series | undefined; label: string; version: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const size = useCanvasSize(ref);
  const [hoverX, setHoverX] = useState<number | null>(null);
  const [tip, setTip] = useState('');

  const left = 66;
  const bottom = 18;

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx || size.width === 0) return;
    ctx.setTransform(size.ratio, 0, 0, size.ratio, 0, 0);
    ctx.clearRect(0, 0, size.width, size.height);
    const plotWidth = size.width - left;
    const plotHeight = size.height - bottom - TOP_PAD;
    let max = 0;
    if (series) for (let i = 0; i < series.filled; i += 1) max = Math.max(max, series.total[(series.head - 1 - i + SERIES_LENGTH * 2) % SERIES_LENGTH]);
    const top = niceMax(max);
    ctx.font = '12px "IBM Plex Sans", sans-serif';
    ctx.textBaseline = 'middle';
    ctx.textAlign = 'right';
    for (const fraction of [0, 0.5, 1]) {
      const y = TOP_PAD + plotHeight * (1 - fraction);
      ctx.strokeStyle = GRID;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(left, Math.round(y) + 0.5);
      ctx.lineTo(size.width, Math.round(y) + 0.5);
      ctx.stroke();
      ctx.fillStyle = MUTED;
      ctx.fillText(fraction === 0 ? '0' : formatSpeed(top * fraction), left - 8, y);
    }
    ctx.textBaseline = 'alphabetic';
    ctx.textAlign = 'left';
    ctx.fillStyle = MUTED;
    const seconds = Math.round(windowOf(series) / 4);
    ctx.fillText(seconds >= 120 ? '2 min atrás' : `${seconds} s atrás`, left, size.height - 4);
    ctx.textAlign = 'right';
    ctx.fillText('agora', size.width, size.height - 4);
    if (!series || series.filled < 2) return;
    const span = windowOf(series);
    const xOf = (age: number) => size.width - (age / (span - 1)) * plotWidth;
    const yOf = (value: number) => TOP_PAD + plotHeight * (1 - value / top);
    const stroke = (buffer: Float32Array, color: string, width: number) => {
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.lineJoin = 'round';
      ctx.beginPath();
      for (let age = 0; age < series.filled; age += 1) {
        const value = buffer[(series.head - 1 - age + SERIES_LENGTH * 2) % SERIES_LENGTH];
        if (age === 0) ctx.moveTo(xOf(age), yOf(value));
        else ctx.lineTo(xOf(age), yOf(value));
      }
      ctx.stroke();
    };
    if (series.links.size > 1) for (const [id, buffer] of series.links) stroke(buffer, store.linkColor(id), 1.5);
    stroke(series.total, series.links.size > 1 ? INK : '#5cc8e6', 2);
    if (hoverX !== null && hoverX >= left) {
      const age = Math.round(((size.width - hoverX) / plotWidth) * (span - 1));
      if (age < series.filled) {
        ctx.strokeStyle = CURSOR;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(Math.round(hoverX) + 0.5, TOP_PAD);
        ctx.lineTo(Math.round(hoverX) + 0.5, TOP_PAD + plotHeight);
        ctx.stroke();
        const value = series.total[(series.head - 1 - age + SERIES_LENGTH * 2) % SERIES_LENGTH];
        setTip(`${formatSpeed(value)}, ${Math.round(age / 4)} s atrás`);
      } else setTip('');
    }
  }, [series, version, size, hoverX]);

  return (
    <div className="chart-wrap">
      <canvas
        ref={ref}
        className="speed-chart"
        role="img"
        aria-label={label}
        onMouseMove={(event) => setHoverX(event.clientX - event.currentTarget.getBoundingClientRect().left)}
        onMouseLeave={() => {
          setHoverX(null);
          setTip('');
        }}
      />
      {hoverX !== null && tip ? <div className="chart-tip" style={{ left: Math.max(0, Math.min(hoverX + 10, size.width - 170)), top: 6 }}>{tip}</div> : null}
    </div>
  );
}
