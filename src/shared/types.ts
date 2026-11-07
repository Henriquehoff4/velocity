export type ItemStatus = 'queued' | 'running' | 'paused' | 'done' | 'failed' | 'needs-link' | 'needs-auth';
export type ItemKind = 'http' | 'torrent' | 'video';
export type Mode = 'auto' | 'turbo' | 'eco' | 'advanced';

export interface ItemLink {
  id: string;
  name: string;
  speed: number;
  connections: number;
  noGain: boolean;
}

export interface Item {
  id: string;
  kind: ItemKind;
  name: string;
  display: string;
  server: string;
  status: ItemStatus;
  phase: string;
  priority: number;
  order: number;
  size: number;
  done: number;
  speed: number;
  average: number;
  max: number;
  eta: number;
  connections: number;
  target: number;
  fragments: number;
  bottleneck: string;
  note: string;
  resumable: boolean;
  verified: boolean;
  checksum: boolean;
  checksumState: string;
  verifyDone: number;
  errorCode: string;
  errorMessage: string;
  folder: string;
  finalPath: string;
  mirrors: number;
  mode: Mode;
  addedAt: number;
  finishedAt: number;
  links?: ItemLink[];
}

export interface NetLinkInfo {
  id: string;
  name: string;
  kind: string;
  ipv4: string;
  ipv6: string;
  gateway: string;
  group: string;
  sharedGroup: boolean;
  nominal: number;
  speed: number;
  osSpeed: number;
  connections: number;
  noGain: boolean;
  virtual: boolean;
  publicIp: string;
}

export interface NetSummary {
  links: NetLinkInfo[];
  independent: number;
  combined: number;
}

export interface MapData {
  id: string;
  size: number;
  done: [number, number][];
  active: { s: number; p: number; e: number; l: string; v: number; w: number }[];
}

export interface AdvancedSettings {
  maxConnections: number;
  minFragmentKiB: number;
  bufferKiB: number;
  limitBps: number;
  ecoLimitBps: number;
  adaptive: boolean;
  multiLink: boolean;
  verify: boolean;
  forceSharedLinks: boolean;
  allowedLinks: string[];
}

export interface Settings {
  mode: Mode;
  folder: string;
  maxConcurrent: number;
  globalLimitBps: number;
  autoResume: boolean;
  notifications: boolean;
  advanced: AdvancedSettings;
}

export interface InspectResult {
  input: string;
  ok: boolean;
  kind?: ItemKind;
  name?: string;
  size?: number;
  server?: string;
  ranges?: boolean;
  html?: boolean;
  contentType?: string;
  digest?: boolean;
  videoReady?: boolean;
  toolReady?: boolean;
  code?: string;
  message?: string;
}

export interface HistoryEntry {
  id: string;
  name: string;
  kind: ItemKind;
  server: string;
  size: number;
  path: string;
  outcome: 'done' | 'failed' | 'cancelled';
  error: string;
  at: number;
  average: number;
  max: number;
  verified: boolean;
  checksum: string;
}

export interface ToolInfo {
  name: string;
  available: boolean;
  version: string;
  bundled: boolean;
}

export interface TickData {
  items: Item[];
  total: number;
  net?: NetSummary;
}

export type EngineEvent =
  | { event: 'tick'; data: TickData }
  | { event: 'item'; data: Item }
  | { event: 'item.removed'; data: { id: string } }
  | { event: 'map'; data: MapData }
  | { event: 'engine.down'; data: { message: string } }
  | { event: 'engine.up'; data: null };

export interface CaptureRequest {
  id: string;
  url: string;
  display: string;
  name: string;
  referrer: string;
}

export interface CaptureState {
  enabled: boolean;
  port: number;
  token: string;
  extensionPath: string;
}

export interface AppInfo {
  version: string;
  isPackaged: boolean;
  testMode: boolean;
  engineReady: boolean;
}

export interface VelocityApi {
  info(): Promise<AppInfo>;
  call<T = unknown>(command: string, args?: unknown): Promise<T>;
  onEvent(listener: (event: EngineEvent) => void): () => void;
  pickFolder(): Promise<string | null>;
  pickTorrent(): Promise<string | null>;
  readClipboard(): Promise<string>;
  writeClipboard(text: string): Promise<void>;
  revealPath(path: string): Promise<boolean>;
  openFolder(path: string): Promise<boolean>;
  captureState(): Promise<CaptureState>;
  captureSet(enabled: boolean): Promise<CaptureState>;
  captureRotate(): Promise<CaptureState>;
  captureAccept(id: string, folder?: string): Promise<void>;
  captureReject(id: string): Promise<void>;
  capturePending(): Promise<CaptureRequest[]>;
  onCapture(listener: (request: CaptureRequest) => void): () => void;
  onFocus(listener: () => void): () => void;
}

declare global {
  interface Window {
    velocity: VelocityApi;
  }
}
