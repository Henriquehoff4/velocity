import { describe, expect, it } from 'vitest';
import { formatBytes, formatEta, formatPercent, formatSpeed, parseSpeedInput, splitSpeed } from '../src/shared/format';
import { extractLinks, isAllowedHost, isAllowedOrigin, parseCapture, safeDisplay } from '../src/shared/links';

describe('extractLinks', () => {
  it('finds links in free text, without duplicates or trailing punctuation', () => {
    const text = 'veja https://a.com/x.zip, e (http://b.org/y.iso). De novo: https://a.com/x.zip\nmagnet:?xt=urn:btih:abc';
    expect(extractLinks(text)).toEqual(['https://a.com/x.zip', 'http://b.org/y.iso', 'magnet:?xt=urn:btih:abc']);
  });

  it('ignores other schemes', () => {
    expect(extractLinks('file:///c:/windows javascript:alert(1) ftp://a.com/x')).toEqual([]);
  });

  it('ignores absurdly long links', () => {
    expect(extractLinks(`https://a.com/${'x'.repeat(20000)}`)).toEqual([]);
  });
});

describe('safeDisplay', () => {
  it('hides query strings and credentials', () => {
    expect(safeDisplay('https://user:secret@a.com/f.zip?token=abc')).toBe('a.com/f.zip');
  });

  it('does not show magnet contents', () => {
    expect(safeDisplay('magnet:?xt=urn:btih:abc')).toBe('Link magnet');
  });

  it('returns empty for invalid input', () => {
    expect(safeDisplay('não é url')).toBe('');
  });
});

describe('parseCapture', () => {
  it('accepts http, https and magnet', () => {
    expect(parseCapture({ url: 'https://a.com/f', name: 'f', referrer: 'https://a.com/' })).toEqual({ url: 'https://a.com/f', name: 'f', referrer: 'https://a.com/' });
    expect(parseCapture({ url: 'magnet:?xt=urn:btih:abc' })?.url).toBe('magnet:?xt=urn:btih:abc');
  });

  it('rejects dangerous or malformed payloads', () => {
    for (const url of ['file:///c:/windows/system32/cmd.exe', 'javascript:alert(1)', 'nada', 42, undefined]) {
      expect(parseCapture({ url })).toBeNull();
    }
    expect(parseCapture(null)).toBeNull();
    expect(parseCapture('https://a.com')).toBeNull();
    expect(parseCapture({ url: `https://a.com/${'x'.repeat(17000)}` })).toBeNull();
  });

  it('drops a referrer that is not http', () => {
    expect(parseCapture({ url: 'https://a.com/f', referrer: 'file:///c:/x' })?.referrer).toBe('');
  });
});

describe('capture origin and host checks', () => {
  it('allows only extensions or no origin', () => {
    expect(isAllowedOrigin(undefined)).toBe(true);
    expect(isAllowedOrigin('chrome-extension://abcdef')).toBe(true);
    expect(isAllowedOrigin('moz-extension://ab-cd')).toBe(true);
    expect(isAllowedOrigin('https://evil.example')).toBe(false);
    expect(isAllowedOrigin('null')).toBe(false);
  });

  it('blocks DNS rebinding through the Host header', () => {
    expect(isAllowedHost('127.0.0.1:17654', 17654)).toBe(true);
    expect(isAllowedHost('localhost:17654', 17654)).toBe(true);
    expect(isAllowedHost('evil.example:17654', 17654)).toBe(false);
    expect(isAllowedHost(undefined, 17654)).toBe(false);
  });
});

describe('format', () => {
  it('formats bytes in pt-BR', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1536)).toBe('1,5 KB');
    expect(formatBytes(150 * 1024 * 1024)).toBe('150 MB');
    expect(formatBytes(-1)).toBe('—');
  });

  it('formats and splits speed', () => {
    expect(formatSpeed(0)).toBe('0 B/s');
    expect(splitSpeed(2.5 * 1024 * 1024)).toEqual({ number: '2,5', unit: 'MB/s' });
  });

  it('formats time left', () => {
    expect(formatEta(-1)).toBe('—');
    expect(formatEta(0.4)).toBe('menos de 1 s');
    expect(formatEta(65)).toBe('1 min 05 s');
    expect(formatEta(3700)).toBe('1 h 01 min');
    expect(formatEta(90000)).toBe('1 d 1 h');
  });

  it('caps the percentage at 100', () => {
    expect(formatPercent(1, 0)).toBe('—');
    expect(formatPercent(50, 200)).toBe('25,0%');
    expect(formatPercent(300, 200)).toBe('100%');
  });

  it('parses speed input', () => {
    expect(parseSpeedInput('')).toBe(0);
    expect(parseSpeedInput('2,5')).toBe(2621440);
    expect(parseSpeedInput('abc')).toBeNull();
    expect(parseSpeedInput('-1')).toBeNull();
  });
});
