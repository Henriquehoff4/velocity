import { useSyncExternalStore } from 'react';
import type { CaptureRequest, EngineEvent, Item, MapData, NetSummary, Settings, ToolInfo } from '../shared/types';

export const SERIES_LENGTH = 480;
export const LINK_COLORS = ['#5cc8e6', '#e8a23b', '#e5728a', '#8fcb6b'];
export const OTHER_LINK_COLOR = '#9aa8b2';

export interface Series {
  total: Float32Array;
  links: Map<string, Float32Array>;
  head: number;
  filled: number;
}

class Store {
  items = new Map<string, Item>();
  selectedId = '';
  net: NetSummary = { links: [], independent: 0, combined: 0 };
  total = 0;
  settings: Settings | null = null;
  tools: ToolInfo[] = [];
  map: MapData | null = null;
  series = new Map<string, Series>();
  captures: CaptureRequest[] = [];
  engineDown = '';
  loadError = '';
  private linkOrder: string[] = [];
  private listeners = new Set<() => void>();
  private version = 0;
  private pendingEmit = false;

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getVersion = () => this.version;

  private emit(): void {
    if (this.pendingEmit) return;
    this.pendingEmit = true;
    queueMicrotask(() => {
      this.pendingEmit = false;
      this.version += 1;
      for (const listener of this.listeners) listener();
    });
  }

  linkColor(id: string): string {
    let index = this.linkOrder.indexOf(id);
    if (index < 0) {
      this.linkOrder.push(id);
      index = this.linkOrder.length - 1;
    }
    return LINK_COLORS[index] ?? OTHER_LINK_COLOR;
  }

  start(): void {
    window.velocity.onEvent((event) => this.onEvent(event));
    window.velocity.onCapture((request) => {
      this.captures = [...this.captures, request];
      this.emit();
    });
    void this.load();
  }

  async load(): Promise<void> {
    try {
      void window.velocity.call<ToolInfo[]>('tools').then((tools) => {
        this.tools = tools;
        this.emit();
      }).catch(() => undefined);
      const [items, settings, net, captures] = await Promise.all([
        window.velocity.call<Item[]>('list'),
        window.velocity.call<Settings>('settings.get'),
        window.velocity.call<NetSummary>('net.list'),
        window.velocity.capturePending(),
      ]);
      this.items = new Map(items.map((item) => [item.id, item]));
      this.settings = settings;
      this.net = net;
      this.captures = captures;
      this.loadError = '';
      if (!this.selectedId && items.length > 0) this.select(items[0].id);
    } catch (error) {
      this.loadError = (error as Error).message;
    }
    this.emit();
  }

  select(id: string): void {
    if (this.selectedId === id) return;
    this.selectedId = id;
    this.map = null;
    void window.velocity.call('watch', { id }).catch(() => undefined);
    this.emit();
  }

  async saveSettings(patch: Record<string, unknown>): Promise<void> {
    const previous = this.settings;
    if (previous) {
      const advanced = patch.advanced as Partial<Settings['advanced']> | undefined;
      this.settings = { ...previous, ...patch, advanced: { ...previous.advanced, ...advanced } } as Settings;
      this.emit();
    }
    try {
      this.settings = await window.velocity.call<Settings>('settings.set', patch);
    } catch (error) {
      this.settings = previous;
      this.loadError = (error as Error).message;
    }
    this.emit();
  }

  dropCapture(id: string): void {
    this.captures = this.captures.filter((capture) => capture.id !== id);
    this.emit();
  }

  private push(item: Item): void {
    let series = this.series.get(item.id);
    if (!series) {
      series = { total: new Float32Array(SERIES_LENGTH), links: new Map(), head: 0, filled: 0 };
      this.series.set(item.id, series);
    }
    const head = series.head;
    series.total[head] = item.speed;
    for (const buffer of series.links.values()) buffer[head] = 0;
    for (const link of item.links ?? []) {
      let buffer = series.links.get(link.id);
      if (!buffer) {
        buffer = new Float32Array(SERIES_LENGTH);
        series.links.set(link.id, buffer);
      }
      buffer[head] = link.speed;
    }
    series.head = (head + 1) % SERIES_LENGTH;
    series.filled = Math.min(SERIES_LENGTH, series.filled + 1);
  }

  private onEvent(event: EngineEvent): void {
    switch (event.event) {
      case 'tick':
        for (const item of event.data.items) {
          const current = this.items.get(item.id);
          if (current) {
            this.items.set(item.id, item);
            if (item.status === 'running' && item.phase === 'downloading') this.push(item);
          }
        }
        this.total = event.data.total;
        if (event.data.net) this.net = event.data.net;
        break;
      case 'item':
        this.items.set(event.data.id, event.data);
        if (!this.selectedId) this.select(event.data.id);
        break;
      case 'item.removed':
        this.items.delete(event.data.id);
        this.series.delete(event.data.id);
        if (this.selectedId === event.data.id) {
          this.selectedId = '';
          const next = this.ordered()[0];
          if (next) this.select(next.id);
        }
        break;
      case 'map':
        if (event.data.id === this.selectedId) this.map = event.data;
        break;
      case 'engine.down':
        this.engineDown = event.data.message;
        break;
      case 'engine.up':
        this.engineDown = '';
        void this.load();
        void window.velocity.call('watch', { id: this.selectedId }).catch(() => undefined);
        break;
    }
    this.emit();
  }

  ordered(): Item[] {
    return [...this.items.values()].sort((a, b) => {
      const doneA = a.status === 'done' ? 1 : 0;
      const doneB = b.status === 'done' ? 1 : 0;
      if (doneA !== doneB) return doneA - doneB;
      if (doneA === 1) return b.finishedAt - a.finishedAt;
      return a.priority - b.priority || a.order - b.order;
    });
  }

  reorderLocal(ids: string[]): void {
    const orders = ids.map((id) => this.items.get(id)?.order ?? 0).sort((a, b) => a - b);
    ids.forEach((id, index) => {
      const item = this.items.get(id);
      if (item) this.items.set(id, { ...item, order: orders[index] });
    });
    this.emit();
  }
}

export const store = new Store();

export function useStore(): Store {
  useSyncExternalStore(store.subscribe, store.getVersion);
  return store;
}
