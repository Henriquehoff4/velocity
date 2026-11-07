const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

function decimal(value: number, digits: number): string {
  return value.toLocaleString('pt-BR', { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value < 0) return '—';
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < UNITS.length - 1) {
    size /= 1024;
    unit += 1;
  }
  if (unit === 0) return `${Math.round(size)} B`;
  return `${decimal(size, size >= 100 ? 0 : 1)} ${UNITS[unit]}`;
}

export function formatSpeed(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '0 B/s';
  return `${formatBytes(value)}/s`;
}

export function splitSpeed(value: number): { number: string; unit: string } {
  const text = formatSpeed(value);
  const at = text.lastIndexOf(' ');
  return { number: text.slice(0, at), unit: text.slice(at + 1) };
}

export function formatEta(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—';
  if (seconds < 1) return 'menos de 1 s';
  const total = Math.round(seconds);
  if (total < 60) return `${total} s`;
  if (total < 3600) return `${Math.floor(total / 60)} min ${String(total % 60).padStart(2, '0')} s`;
  if (total < 86400) return `${Math.floor(total / 3600)} h ${String(Math.floor((total % 3600) / 60)).padStart(2, '0')} min`;
  return `${Math.floor(total / 86400)} d ${Math.floor((total % 86400) / 3600)} h`;
}

export function formatPercent(done: number, size: number): string {
  if (size <= 0) return '—';
  const value = Math.min(100, (done / size) * 100);
  return `${decimal(value, value >= 100 ? 0 : 1)}%`;
}

export function formatDate(ms: number): string {
  if (!ms) return '—';
  return new Date(ms).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
}

export function parseSpeedInput(text: string): number | null {
  const trimmed = text.trim().replace(',', '.');
  if (trimmed === '' || trimmed === '0') return 0;
  const value = Number(trimmed);
  if (!Number.isFinite(value) || value < 0 || value > 1e6) return null;
  return Math.round(value * 1024 * 1024);
}
