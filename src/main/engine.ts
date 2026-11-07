import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import type { EngineEvent } from '../shared/types';

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout | null;
}

export class EngineError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

export class Engine {
  private child: ChildProcessWithoutNullStreams | null = null;
  private pending = new Map<number, Pending>();
  private nextId = 1;
  private buffer = '';
  private stopping = false;
  private restarts = 0;

  constructor(
    private command: string,
    private args: string[],
    private env: NodeJS.ProcessEnv,
    private onEvent: (event: EngineEvent) => void,
  ) {}

  get ready(): boolean {
    return this.child !== null;
  }

  start(): void {
    const child = spawn(this.command, this.args, { stdio: 'pipe', windowsHide: true, env: this.env, shell: false });
    this.child = child;
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => this.onData(chunk));
    child.stderr.on('data', () => undefined);
    child.stdin.on('error', () => undefined);
    child.on('error', (error) => this.onExit(error.message));
    child.on('exit', (code) => this.onExit(`O motor encerrou com código ${code ?? 'desconhecido'}.`));
  }

  private onExit(message: string): void {
    if (!this.child) return;
    this.child = null;
    this.buffer = '';
    for (const entry of this.pending.values()) {
      if (entry.timer) clearTimeout(entry.timer);
      entry.reject(new EngineError('engine-down', 'O motor de downloads reiniciou. Tente de novo.'));
    }
    this.pending.clear();
    if (this.stopping) return;
    this.onEvent({ event: 'engine.down', data: { message } });
    this.restarts += 1;
    setTimeout(() => {
      if (this.stopping || this.child) return;
      this.start();
      this.onEvent({ event: 'engine.up', data: null });
    }, Math.min(10000, 500 * this.restarts));
  }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let index = this.buffer.indexOf('\n');
    while (index >= 0) {
      const line = this.buffer.slice(0, index);
      this.buffer = this.buffer.slice(index + 1);
      if (line.length > 0) this.onLine(line);
      index = this.buffer.indexOf('\n');
    }
  }

  private onLine(line: string): void {
    let message: { id?: number; ok?: boolean; data?: unknown; error?: { code: string; message: string }; event?: string };
    try {
      message = JSON.parse(line);
    } catch {
      return;
    }
    if (message.event) {
      this.onEvent(message as EngineEvent);
      return;
    }
    const entry = message.id !== undefined ? this.pending.get(message.id) : undefined;
    if (!entry || message.id === undefined) return;
    this.pending.delete(message.id);
    if (entry.timer) clearTimeout(entry.timer);
    if (message.ok) entry.resolve(message.data ?? null);
    else entry.reject(new EngineError(message.error?.code ?? 'internal', message.error?.message ?? 'Erro no motor.'));
  }

  call<T>(cmd: string, args?: unknown, timeoutMs = 60000): Promise<T> {
    if (!this.child) return Promise.reject(new EngineError('engine-down', 'O motor de downloads não está em execução.'));
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new EngineError('timeout', 'O motor demorou demais para responder.'));
      }, timeoutMs);
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer });
      this.child?.stdin.write(`${JSON.stringify({ id, cmd, args: args ?? {} })}\n`);
    });
  }

  async stop(): Promise<void> {
    this.stopping = true;
    const child = this.child;
    if (!child) return;
    await new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        child.kill();
        resolve();
      }, 6000);
      child.once('exit', () => {
        clearTimeout(timer);
        resolve();
      });
      child.stdin.write(`${JSON.stringify({ id: 0, cmd: 'shutdown' })}\n`);
      child.stdin.end();
    });
  }
}
