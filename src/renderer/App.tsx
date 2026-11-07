import { useState } from 'react';
import { formatSpeed } from '../shared/format';
import type { Mode } from '../shared/types';
import { DownloadsView } from './Downloads';
import { HistoryView } from './History';
import { NetworksView } from './Networks';
import { SettingsView } from './SettingsView';
import { Logo } from './ui';
import { store, useStore } from './store';

type View = 'downloads' | 'networks' | 'history' | 'settings';

const NAV: { id: View; label: string }[] = [
  { id: 'downloads', label: 'Downloads' },
  { id: 'networks', label: 'Redes' },
  { id: 'history', label: 'Histórico' },
  { id: 'settings', label: 'Ajustes' },
];

export const MODES: { id: Mode; label: string; hint: string }[] = [
  { id: 'auto', label: 'Automático', hint: 'Mede e ajusta as conexões sozinho.' },
  { id: 'turbo', label: 'Turbo', hint: 'Até 32 conexões, sobe mais rápido e recua se o servidor reclamar.' },
  { id: 'eco', label: 'Econômico', hint: 'Até 2 conexões e limite de banda.' },
  { id: 'advanced', label: 'Avançado', hint: 'Você define conexões, limites e redes em Ajustes.' },
];

export function App() {
  const state = useStore();
  const [view, setView] = useState<View>('downloads');
  const mode = state.settings?.mode ?? 'auto';
  const items = [...state.items.values()];
  const anyRunning = items.some((item) => item.status === 'running' || item.status === 'queued');
  const anyPaused = items.some((item) => item.status === 'paused');
  const running = items.filter((item) => item.status === 'running').length;

  return (
    <div className="shell">
      <aside className="side">
        <div className="brand">
          <Logo />
          <span>Velocity</span>
        </div>
        <nav aria-label="Seções">
          {NAV.map((entry) => (
            <button key={entry.id} type="button" className={`nav-item${view === entry.id ? ' on' : ''}`} aria-current={view === entry.id ? 'page' : undefined} onClick={() => setView(entry.id)}>
              {entry.label}
              {entry.id === 'downloads' && running > 0 ? <span className="nav-count">{running}</span> : null}
            </button>
          ))}
        </nav>
        <fieldset className="modes">
          <legend>Modo de velocidade</legend>
          {MODES.map((entry) => (
            <label key={entry.id} className={`mode${mode === entry.id ? ' on' : ''}`}>
              <input type="radio" name="mode" value={entry.id} checked={mode === entry.id} onChange={() => void store.saveSettings({ mode: entry.id })} />
              <span>{entry.label}</span>
            </label>
          ))}
          <p className="mode-hint">{MODES.find((entry) => entry.id === mode)?.hint}</p>
        </fieldset>
      </aside>
      <header className="top">
        <h1>{NAV.find((entry) => entry.id === view)?.label}</h1>
        <div className="top-right">
          <div className="top-speed" aria-live="off">
            <span className="muted">Total agora</span>
            <strong className="num">{formatSpeed(state.total)}</strong>
          </div>
          {view === 'downloads' ? (
            <>
              <button type="button" className="btn" disabled={!anyRunning} onClick={() => void window.velocity.call('pauseAll')}>Pausar tudo</button>
              <button type="button" className="btn" disabled={!anyPaused} onClick={() => void window.velocity.call('resumeAll')}>Continuar tudo</button>
            </>
          ) : null}
        </div>
      </header>
      <main className="sheet">
        {state.engineDown ? <div className="banner bad" role="alert">O motor de downloads parou e está reiniciando. Os downloads continuam de onde pararam.</div> : null}
        {state.loadError ? (
          <div className="banner bad" role="alert">
            Não foi possível carregar os dados: {state.loadError}
            <button type="button" className="btn" onClick={() => void store.load()}>Tentar de novo</button>
          </div>
        ) : null}
        {view === 'downloads' ? <DownloadsView /> : null}
        {view === 'networks' ? <NetworksView /> : null}
        {view === 'history' ? <HistoryView /> : null}
        {view === 'settings' ? <SettingsView /> : null}
      </main>
    </div>
  );
}
