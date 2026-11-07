const LINK_PATTERN = /(?:https?:\/\/|magnet:\?)[^\s"'<>]+/gi;

export function extractLinks(text: string): string[] {
  const found = text.match(LINK_PATTERN) ?? [];
  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of found) {
    const clean = raw.replace(/[),.;]+$/, '');
    if (clean.length > 16384 || seen.has(clean)) continue;
    seen.add(clean);
    result.push(clean);
  }
  return result;
}

export function safeDisplay(url: string): string {
  try {
    const parsed = new URL(url);
    if (parsed.protocol === 'magnet:') return 'Link magnet';
    return `${parsed.host}${parsed.pathname}`;
  } catch {
    return '';
  }
}

export interface CapturePayload {
  url: string;
  name: string;
  referrer: string;
}

export function parseCapture(body: unknown): CapturePayload | null {
  if (typeof body !== 'object' || body === null) return null;
  const { url, name, referrer } = body as Record<string, unknown>;
  if (typeof url !== 'string' || url.length > 16384) return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:' && parsed.protocol !== 'magnet:') return null;
  return {
    url,
    name: typeof name === 'string' ? name.slice(0, 260) : '',
    referrer: typeof referrer === 'string' && /^https?:\/\//i.test(referrer) ? referrer.slice(0, 4096) : '',
  };
}

export function isAllowedOrigin(origin: string | undefined): boolean {
  if (origin === undefined || origin === '') return true;
  return /^(chrome|moz)-extension:\/\/[a-z0-9-]+\/?$/i.test(origin);
}

export function isAllowedHost(host: string | undefined, port: number): boolean {
  return host === `127.0.0.1:${port}` || host === `localhost:${port}`;
}
