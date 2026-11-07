import { BrowserWindow, Menu, Notification, app, clipboard, dialog, ipcMain, session, shell } from 'electron';
import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import type { AppInfo, CaptureState, EngineEvent, Item } from '../shared/types';
import { CaptureServer, newToken } from './capture';
import { Engine } from './engine';

const GROUND = '#0f1418';
const CAPTURE_PORT = 17654;
const TEST_MODE = !app.isPackaged && process.env.VELOCITY_TEST === '1';
const ALLOWED_COMMANDS = new Set([
  'list', 'inspect', 'add', 'pause', 'resume', 'cancel', 'remove', 'pauseAll', 'resumeAll', 'clearDone', 'priority', 'move', 'reorder',
  'updateLink', 'setAuth', 'watch', 'settings.get', 'settings.set', 'net.list', 'net.checkPublic', 'history.list', 'history.clear', 'tools',
]);

if (TEST_MODE && process.env.VELOCITY_USER_DATA) app.setPath('userData', process.env.VELOCITY_USER_DATA);
if (!app.requestSingleInstanceLock()) app.quit();

let win: BrowserWindow | null = null;
let engine: Engine;
let quitting = false;
let notify = true;
let captureConfig = { enabled: false, token: newToken() };
const statusById = new Map<string, string>();

const captureFile = () => path.join(app.getPath('userData'), 'capture.json');
const iconPath = () => (app.isPackaged ? path.join(process.resourcesPath, 'icon.png') : path.join(app.getAppPath(), 'build', 'icon.png'));

const capture = new CaptureServer(
  () => captureConfig.token,
  (request) => {
    win?.webContents.send('capture:request', request);
    if (win?.isMinimized()) win.restore();
    if (notify && Notification.isSupported()) new Notification({ title: 'Download capturado do navegador', body: `Confirme no Velocity: ${request.name}` }).show();
  },
);

function loadCapture(): void {
  try {
    const stored = JSON.parse(readFileSync(captureFile(), 'utf8'));
    if (typeof stored.token === 'string' && /^[0-9a-f]{48}$/.test(stored.token)) captureConfig = { enabled: stored.enabled === true, token: stored.token };
  } catch {
    captureConfig = { enabled: false, token: newToken() };
  }
}

function saveCapture(): void {
  writeFileSync(captureFile(), JSON.stringify(captureConfig), { mode: 0o600 });
}

async function captureState(): Promise<CaptureState> {
  if (captureConfig.enabled && !capture.running) await capture.start(CAPTURE_PORT);
  if (!captureConfig.enabled && capture.running) capture.stop();
  const extensionPath = app.isPackaged ? path.join(process.resourcesPath, 'browser-extension') : path.join(app.getAppPath(), 'browser-extension');
  return { enabled: captureConfig.enabled, port: capture.running ? capture.port : 0, token: captureConfig.token, extensionPath };
}

function engineCommand(): { command: string; args: string[]; tools: string } {
  const data = path.join(app.getPath('userData'), 'engine');
  if (app.isPackaged) {
    return {
      command: path.join(process.resourcesPath, 'engine', 'velocity-engine.exe'),
      args: ['--data', data],
      tools: path.join(process.resourcesPath, 'tools'),
    };
  }
  const root = app.getAppPath();
  const published = path.join(root, 'engine', 'publish', 'velocity-engine.exe');
  const built = path.join(root, 'engine', 'Velocity.Engine', 'bin', 'Release', 'net8.0-windows10.0.17763.0', 'win-x64', 'velocity-engine.exe');
  const useBuilt = TEST_MODE && existsSync(built);
  return { command: useBuilt || !existsSync(published) ? built : published, args: ['--data', data], tools: path.join(root, 'resources', 'tools') };
}

function engineEnv(tools: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const key of ['SystemRoot', 'SystemDrive', 'TEMP', 'TMP', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'PATH', 'Path', 'ProgramData', 'ProgramFiles', 'HOMEDRIVE', 'HOMEPATH', 'COMPUTERNAME', 'USERNAME', 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY']) {
    if (process.env[key]) env[key] = process.env[key];
  }
  env.VELOCITY_TOOLS = tools;
  if (TEST_MODE) {
    env.VELOCITY_TEST = '1';
    if (process.env.VELOCITY_TEST_LINKS) env.VELOCITY_TEST_LINKS = process.env.VELOCITY_TEST_LINKS;
  }
  return env;
}

function onEngineEvent(event: EngineEvent): void {
  if (event.event === 'item') maybeNotify(event.data);
  if (win && !win.isDestroyed()) win.webContents.send('engine:event', event);
}

function maybeNotify(item: Item): void {
  const previous = statusById.get(item.id);
  statusById.set(item.id, item.status);
  if (previous === item.status || !notify || !Notification.isSupported() || TEST_MODE) return;
  if (item.status === 'done') {
    const notification = new Notification({ title: 'Download concluído', body: item.name, icon: iconPath() });
    notification.on('click', () => {
      win?.show();
      if (item.finalPath) shell.showItemInFolder(item.finalPath);
    });
    notification.show();
  } else if (item.status === 'failed' || item.status === 'needs-link' || item.status === 'needs-auth') {
    new Notification({ title: 'Download parado', body: `${item.name}: ${item.errorMessage}`, icon: iconPath() }).show();
  }
}

function createWindow(): void {
  win = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 1024,
    minHeight: 640,
    title: 'Velocity',
    backgroundColor: GROUND,
    icon: iconPath(),
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
      spellcheck: false,
    },
  });
  win.once('ready-to-show', () => win?.show());
  win.on('focus', () => win?.webContents.send('window:focus', null));
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('will-navigate', (event) => event.preventDefault());
  void win.loadFile(path.join(app.getAppPath(), 'dist', 'index.html'));
}

function registerIpc(): void {
  const fromMainFrame = (event: Electron.IpcMainInvokeEvent) => event.senderFrame === win?.webContents.mainFrame;
  ipcMain.handle('app:info', (): AppInfo => ({ version: app.getVersion(), isPackaged: app.isPackaged, testMode: TEST_MODE, engineReady: engine.ready }));
  ipcMain.handle('engine:call', async (event, command: unknown, args: unknown) => {
    if (!fromMainFrame(event)) throw new Error('Origem não autorizada.');
    if (typeof command !== 'string' || !ALLOWED_COMMANDS.has(command)) throw new Error('Comando não permitido.');
    const timeout = command === 'inspect' || command === 'net.checkPublic' || command === 'updateLink' ? 120000 : 30000;
    try {
      const result = await engine.call(command, args, timeout);
      if (command === 'settings.get' || command === 'settings.set') notify = (result as { notifications: boolean }).notifications;
      return { ok: true, data: result };
    } catch (error) {
      const failure = error as { code?: string; message?: string };
      return { ok: false, code: failure.code ?? 'internal', message: failure.message ?? 'Erro desconhecido.' };
    }
  });
  ipcMain.handle('dialog:folder', async (event) => {
    if (!fromMainFrame(event)) return null;
    if (TEST_MODE && process.env.VELOCITY_TEST_FOLDER) return process.env.VELOCITY_TEST_FOLDER;
    const result = await dialog.showOpenDialog(win as BrowserWindow, { title: 'Escolha a pasta de destino', properties: ['openDirectory', 'createDirectory'] });
    return result.canceled ? null : result.filePaths[0];
  });
  ipcMain.handle('dialog:torrent', async (event) => {
    if (!fromMainFrame(event)) return null;
    const result = await dialog.showOpenDialog(win as BrowserWindow, { title: 'Escolha um arquivo .torrent', properties: ['openFile'], filters: [{ name: 'Torrent', extensions: ['torrent'] }] });
    return result.canceled ? null : result.filePaths[0];
  });
  ipcMain.handle('clipboard:read', async (event) => {
    if (!fromMainFrame(event)) return '';
    return (await clipboard.readText()).slice(0, 200000);
  });
  ipcMain.handle('clipboard:write', async (event, text: unknown) => {
    if (fromMainFrame(event) && typeof text === 'string' && text.length <= 200000) await clipboard.writeText(text);
  });
  ipcMain.handle('shell:reveal', (event, target: unknown) => {
    if (!fromMainFrame(event) || typeof target !== 'string' || !path.isAbsolute(target) || !existsSync(target)) return false;
    shell.showItemInFolder(target);
    return true;
  });
  ipcMain.handle('shell:folder', async (event, target: unknown) => {
    if (!fromMainFrame(event) || typeof target !== 'string' || !path.isAbsolute(target) || !existsSync(target) || !statSync(target).isDirectory()) return false;
    return (await shell.openPath(target)) === '';
  });
  ipcMain.handle('capture:state', (event) => {
    if (!fromMainFrame(event)) throw new Error('Origem não autorizada.');
    return captureState();
  });
  ipcMain.handle('capture:set', async (event, enabled: unknown) => {
    if (!fromMainFrame(event)) throw new Error('Origem não autorizada.');
    captureConfig.enabled = enabled === true;
    saveCapture();
    return captureState();
  });
  ipcMain.handle('capture:rotate', async (event) => {
    if (!fromMainFrame(event)) throw new Error('Origem não autorizada.');
    captureConfig.token = newToken();
    saveCapture();
    return captureState();
  });
  ipcMain.handle('capture:pending', (event) => (fromMainFrame(event) ? capture.list() : []));
  ipcMain.handle('capture:accept', async (event, id: unknown, folder: unknown) => {
    if (!fromMainFrame(event) || typeof id !== 'string') return;
    const taken = capture.take(id);
    if (!taken) return;
    await engine.call('add', { urls: [taken.url], referer: taken.referrer, ...(typeof folder === 'string' ? { folder } : {}) });
  });
  ipcMain.handle('capture:reject', (event, id: unknown) => {
    if (fromMainFrame(event) && typeof id === 'string') capture.take(id);
  });
}

function hardenSession(): void {
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.defaultSession.webRequest.onBeforeRequest((details, callback) => {
    callback({ cancel: !details.url.startsWith('file:') && !details.url.startsWith('devtools:') });
  });
}

app.on('second-instance', () => {
  if (win?.isMinimized()) win.restore();
  win?.focus();
});

app.whenReady().then(async () => {
  Menu.setApplicationMenu(null);
  hardenSession();
  loadCapture();
  const { command, args, tools } = engineCommand();
  engine = new Engine(command, args, engineEnv(tools), onEngineEvent);
  engine.start();
  registerIpc();
  await captureState().catch(() => undefined);
  createWindow();
});

app.on('before-quit', (event) => {
  if (quitting) return;
  quitting = true;
  event.preventDefault();
  capture.stop();
  void engine.stop().finally(() => app.exit(0));
});

app.on('window-all-closed', () => app.quit());
