import { contextBridge, ipcRenderer } from 'electron';
import type { CaptureRequest, EngineEvent, VelocityApi } from '../shared/types';

function subscribe<T>(channel: string, listener: (value: T) => void): () => void {
  const handler = (_event: unknown, value: T) => listener(value);
  ipcRenderer.on(channel, handler);
  return () => ipcRenderer.removeListener(channel, handler);
}

const api: VelocityApi = {
  info: () => ipcRenderer.invoke('app:info'),
  call: async <T,>(command: string, args?: unknown): Promise<T> => {
    const reply = (await ipcRenderer.invoke('engine:call', command, args)) as { ok: boolean; data?: T; code?: string; message?: string };
    if (reply.ok) return reply.data as T;
    throw new Error(reply.message ?? 'Erro desconhecido.');
  },
  onEvent: (listener) => subscribe<EngineEvent>('engine:event', listener),
  pickFolder: () => ipcRenderer.invoke('dialog:folder'),
  pickTorrent: () => ipcRenderer.invoke('dialog:torrent'),
  readClipboard: () => ipcRenderer.invoke('clipboard:read'),
  writeClipboard: (text) => ipcRenderer.invoke('clipboard:write', text),
  revealPath: (path) => ipcRenderer.invoke('shell:reveal', path),
  openFolder: (path) => ipcRenderer.invoke('shell:folder', path),
  captureState: () => ipcRenderer.invoke('capture:state'),
  captureSet: (enabled) => ipcRenderer.invoke('capture:set', enabled),
  captureRotate: () => ipcRenderer.invoke('capture:rotate'),
  captureAccept: (id, folder) => ipcRenderer.invoke('capture:accept', id, folder),
  captureReject: (id) => ipcRenderer.invoke('capture:reject', id),
  capturePending: () => ipcRenderer.invoke('capture:pending'),
  onCapture: (listener) => subscribe<CaptureRequest>('capture:request', listener),
  onFocus: (listener) => subscribe<null>('window:focus', () => listener()),
};

contextBridge.exposeInMainWorld('velocity', api);
