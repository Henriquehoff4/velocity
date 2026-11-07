import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { CaptureServer } from '../src/main/capture';
import type { CaptureRequest } from '../src/shared/types';

const TOKEN = 'a'.repeat(48);
let server: CaptureServer;
let received: CaptureRequest[];

function post(body: unknown, headers: Record<string, string> = {}) {
  return fetch(`http://127.0.0.1:${server.port}/capture`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}`, ...headers },
    body: JSON.stringify(body),
  });
}

beforeEach(async () => {
  received = [];
  server = new CaptureServer(() => TOKEN, (request) => received.push(request));
  await server.start(0);
});

afterEach(() => server.stop());

describe('CaptureServer', () => {
  it('queues a valid request for confirmation and does not expose the URL to the list', async () => {
    const reply = await post({ url: 'https://a.com/f.zip?token=secreto', name: 'f.zip' });
    expect(reply.status).toBe(202);
    expect(received).toHaveLength(1);
    expect(JSON.stringify(server.list())).not.toContain('secreto');
    expect(server.take(received[0].id)?.url).toBe('https://a.com/f.zip?token=secreto');
    expect(server.list()).toHaveLength(0);
  });

  it('rejects a missing or wrong token', async () => {
    expect((await post({ url: 'https://a.com/f' }, { Authorization: 'Bearer errado' })).status).toBe(401);
    expect((await post({ url: 'https://a.com/f' }, { Authorization: '' })).status).toBe(401);
    expect(received).toHaveLength(0);
  });

  it('rejects web page origins', async () => {
    expect((await post({ url: 'https://a.com/f' }, { Origin: 'https://evil.example' })).status).toBe(403);
    expect(received).toHaveLength(0);
  });

  it('rejects unsafe schemes and invalid JSON', async () => {
    expect((await post({ url: 'file:///c:/windows/win.ini' })).status).toBe(400);
    const invalid = await fetch(`http://127.0.0.1:${server.port}/capture`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
      body: '{',
    });
    expect(invalid.status).toBe(400);
  });

  it('requires JSON content, which blocks simple cross-site forms', async () => {
    const reply = await fetch(`http://127.0.0.1:${server.port}/capture`, {
      method: 'POST',
      headers: { 'Content-Type': 'text/plain', Authorization: `Bearer ${TOKEN}` },
      body: '{"url":"https://a.com/f"}',
    });
    expect(reply.status).toBe(404);
  });

  it('limits pending requests', async () => {
    let last = 0;
    for (let i = 0; i < 51; i += 1) last = (await post({ url: `https://a.com/${i}` })).status;
    expect(last).toBe(429);
    expect(server.list()).toHaveLength(50);
  });
});
