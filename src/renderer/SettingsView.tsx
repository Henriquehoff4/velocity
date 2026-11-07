import { useEffect, useState } from 'react';
import { parseSpeedInput } from '../shared/format';
import type { AdvancedSettings, CaptureState } from '../shared/types';
import { store, useStore } from './store';

function mbValue(bps: number): string {
  return bps > 0 ? String(Math.round((bps / (1024 * 1024)) * 100) / 100).replace('.', ',') : '';
}

function SpeedField({ label, value, onCommit, hint }: { label: string; value: number; onCommit: (bps: number) => void; hint: string }) {
  const [text, setText] = useState(mbValue(value));
  const [error, setError] = useState(false);
  useEffect(() => setText(mbValue(value)), [value]);
  return (
    <label className="field">
      {label}
      <input
        value={text}
        inputMode="decimal"
        aria-invalid={error}
        placeholder="Sem limite"
        onChange={(event) => setText(event.target.value)}
        onBlur={() => {
          const parsed = parseSpeedInput(text);
          setError(parsed === null);
          if (parsed !== null) onCommit(parsed);
        }}
      />
      <small className={error ? 'bad-text' : 'muted'}>{error ? 'Use um número em MB/s, como 2,5.' : hint}</small>
    </label>
  );
}

function NumberField({ label, value, min, max, disabled, onCommit, hint }: { label: string; value: number; min: number; max: number; disabled?: boolean; onCommit: (value: number) => void; hint?: string }) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  return (
    <label className="field">
      {label}
      <input
        type="number"
        min={min}
        max={max}
        value={text}
        disabled={disabled}
        onChange={(event) => setText(event.target.value)}
        onBlur={() => {
          const parsed = Math.max(min, Math.min(max, Number(text) || min));
          setText(String(parsed));
          onCommit(parsed);
        }}
      />
      {hint ? <small className="muted">{hint}</small> : null}
    </label>
  );
}

export function SettingsView() {
  const state = useStore();
  const settings = state.settings;
  const [capture, setCapture] = useState<CaptureState | null>(null);
  const [captureError, setCaptureError] = useState('');
  const [copied, setCopied] = useState('');
  useEffect(() => {
    void window.velocity.captureState().then(setCapture);
  }, []);
  if (!settings) return <div className="page"><p className="muted" role="status">Abrindo os ajustes…</p></div>;
  const advanced = settings.advanced;
  const isAdvanced = settings.mode === 'advanced';
  const setAdvanced = (patch: Partial<AdvancedSettings>) => void store.saveSettings({ advanced: patch });
  const copy = (label: string, text: string) => {
    void window.velocity.writeClipboard(text);
    setCopied(label);
    setTimeout(() => setCopied(''), 1500);
  };
  const links = state.net.links;

  return (
    <div className="page settings">
      <section className="group">
        <h2>Fila e destino</h2>
        <div className="grid">
          <div className="field wide">
            Pasta de destino
            <div className="inline">
              <span className="path ellipsis" title={settings.folder}>{settings.folder}</span>
              <button type="button" className="btn" onClick={() => void window.velocity.pickFolder().then((folder) => { if (folder) void store.saveSettings({ folder }); })}>Trocar pasta</button>
            </div>
          </div>
          <NumberField label="Downloads ao mesmo tempo" value={settings.maxConcurrent} min={1} max={16} onCommit={(value) => void store.saveSettings({ maxConcurrent: value })} />
          <SpeedField label="Limite total, em MB/s" value={settings.globalLimitBps} hint="Vale para todos os downloads somados." onCommit={(value) => void store.saveSettings({ globalLimitBps: value })} />
          <SpeedField label="Limite no modo Econômico, em MB/s" value={advanced.ecoLimitBps} hint="Por download." onCommit={(value) => setAdvanced({ ecoLimitBps: value })} />
        </div>
        <label className="check"><input type="checkbox" checked={settings.autoResume} onChange={(event) => void store.saveSettings({ autoResume: event.target.checked })} /> Continuar os downloads ao abrir o Velocity</label>
        <label className="check"><input type="checkbox" checked={settings.notifications} onChange={(event) => void store.saveSettings({ notifications: event.target.checked })} /> Avisar quando um download terminar ou parar</label>
      </section>

      <section className="group">
        <h2>Modo Avançado</h2>
        <p className="muted group-hint">{isAdvanced ? 'Estes valores estão em uso.' : 'Só valem no modo Avançado. Escolha o modo na barra lateral.'}</p>
        <fieldset className="plain" disabled={!isAdvanced}>
          <div className="grid">
            <NumberField label="Máximo de conexões" value={advanced.maxConnections} min={1} max={64} onCommit={(value) => setAdvanced({ maxConnections: value })} />
            <NumberField label="Fragmento mínimo, em KB" value={advanced.minFragmentKiB} min={64} max={1048576} onCommit={(value) => setAdvanced({ minFragmentKiB: value })} />
            <NumberField label="Buffer por conexão, em KB" value={advanced.bufferKiB} min={64} max={4096} onCommit={(value) => setAdvanced({ bufferKiB: value })} />
            <SpeedField label="Limite por download, em MB/s" value={advanced.limitBps} hint="Em branco, sem limite." onCommit={(value) => setAdvanced({ limitBps: value })} />
          </div>
          <label className="check"><input type="checkbox" checked={advanced.adaptive} onChange={(event) => setAdvanced({ adaptive: event.target.checked })} /> Ajustar conexões conforme a medição. Desligado, usa sempre o máximo acima. Um 429 do servidor reduz mesmo assim.</label>
          <label className="check"><input type="checkbox" checked={advanced.verify} onChange={(event) => setAdvanced({ verify: event.target.checked })} /> Reler e conferir os dados do disco ao terminar</label>
          <label className="check"><input type="checkbox" checked={advanced.multiLink} onChange={(event) => setAdvanced({ multiLink: event.target.checked })} /> Usar mais de uma rede</label>
          <label className="check"><input type="checkbox" checked={advanced.forceSharedLinks} onChange={(event) => setAdvanced({ forceSharedLinks: event.target.checked })} /> Usar também redes que parecem dividir o mesmo acesso</label>
          {links.length > 0 ? (
            <div className="field wide">
              Redes permitidas (nenhuma marcada: todas as físicas)
              {links.map((link) => (
                <label key={link.id} className="check">
                  <input
                    type="checkbox"
                    checked={advanced.allowedLinks.includes(link.id)}
                    onChange={(event) => setAdvanced({ allowedLinks: event.target.checked ? [...advanced.allowedLinks, link.id] : advanced.allowedLinks.filter((id) => id !== link.id) })}
                  />
                  {link.name}
                </label>
              ))}
            </div>
          ) : null}
        </fieldset>
      </section>

      <section className="group">
        <h2>Captura do navegador</h2>
        <p className="muted group-hint">Uma extensão para Chrome, Edge e similares envia os downloads para cá. Cada um espera sua confirmação. Cookies não são enviados, então sites que exigem login podem recusar o download.</p>
        <label className="check">
          <input
            type="checkbox"
            checked={capture?.enabled ?? false}
            onChange={(event) => {
              const enabled = event.target.checked;
              const previous = capture;
              setCaptureError('');
              if (capture) setCapture({ ...capture, enabled });
              window.velocity.captureSet(enabled).then(setCapture).catch((failure: Error) => {
                setCapture(previous);
                setCaptureError(failure.message);
              });
            }}
          />
          Receber downloads da extensão
        </label>
        {captureError ? <p className="form-error" role="alert">{captureError}</p> : null}
        {capture?.enabled ? (
          <>
            <ol className="steps">
              <li>Abra <span className="mono">chrome://extensions</span>, ligue o modo do desenvolvedor e use Carregar sem compactação.</li>
              <li>
                Escolha a pasta <span className="mono path-inline">{capture.extensionPath}</span>{' '}
                <button type="button" className="link" onClick={() => copy('pasta', capture.extensionPath)}>{copied === 'pasta' ? 'Copiada' : 'Copiar pasta'}</button>
              </li>
              <li>
                Nas opções da extensão, informe a porta <span className="mono">{capture.port}</span> e o código de acesso.{' '}
                <button type="button" className="link" onClick={() => copy('codigo', capture.token)}>{copied === 'codigo' ? 'Copiado' : 'Copiar código'}</button>
              </li>
            </ol>
            <button type="button" className="btn" onClick={() => void window.velocity.captureRotate().then(setCapture)}>Gerar novo código</button>
          </>
        ) : null}
      </section>

      <section className="group">
        <h2>Ferramentas opcionais</h2>
        <p className="muted group-hint">O aria2 cuida de torrents e links magnet. O yt-dlp baixa vídeos de páginas, e o ffmpeg junta áudio e vídeo. Downloads http e https usam o motor do Velocity e não dependem delas.</p>
        <table className="table compact">
          <thead><tr><th scope="col">Ferramenta</th><th scope="col">Situação</th><th scope="col">Versão</th></tr></thead>
          <tbody>
            {state.tools.length === 0 ? <tr><td colSpan={3} className="muted">Procurando as ferramentas…</td></tr> : null}
            {state.tools.map((tool) => (
              <tr key={tool.name}>
                <td className="mono">{tool.name}</td>
                <td className={tool.available ? '' : 'bad-text'}>{tool.available ? (tool.bundled ? 'Incluída no Velocity' : 'Encontrada no computador') : 'Não encontrada'}</td>
                <td className="muted ellipsis version">{tool.version}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
