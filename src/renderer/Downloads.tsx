import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react';
import { formatBytes, formatEta, formatPercent, formatSpeed, splitSpeed } from '../shared/format';
import { extractLinks, safeDisplay } from '../shared/links';
import type { InspectResult, Item } from '../shared/types';
import { FragmentMap, SpeedChart } from './canvas';
import { store, useStore } from './store';
import { Icon, IconButton, Kpi } from './ui';

const STATUS: Record<string, string> = {
  queued: 'Na fila',
  running: 'Baixando',
  paused: 'Pausado',
  done: 'Concluído',
  failed: 'Falhou',
  'needs-link': 'Precisa de link novo',
  'needs-auth': 'Precisa de login',
};

const PHASE: Record<string, string> = {
  connecting: 'Conectando',
  downloading: 'Baixando',
  verifying: 'Conferindo os dados',
  checksum: 'Conferindo o checksum',
  finalizing: 'Finalizando',
  metadata: 'Buscando dados do torrent',
};

const BOTTLENECK: Record<string, string> = {
  'server-limit': 'O servidor pediu menos conexões. O Velocity reduziu e está aguardando o tempo que ele indicou.',
  'speed-limit': 'Você definiu um limite de velocidade, e ele está sendo atingido.',
  disk: 'O disco de destino está limitando a velocidade: as gravações demoram mais do que a chegada dos dados.',
  link: 'Sua conexão está perto da capacidade do adaptador de rede.',
  saturated: 'Mais conexões não aumentaram a velocidade. Provavelmente este é o limite da sua internet ou do servidor.',
};

const PRIORITY = ['Alta', 'Normal', 'Baixa'];

function statusLine(item: Item): string {
  if (item.status === 'running') return PHASE[item.phase] ?? 'Baixando';
  return STATUS[item.status] ?? item.status;
}

function isActive(item: Item): boolean {
  return item.status === 'running';
}

export function DownloadsView() {
  const state = useStore();
  const items = state.ordered();
  const selected = state.items.get(state.selectedId);
  return (
    <div className="downloads">
      <LinkBar />
      <Captures />
      {items.length === 0 ? (
        <div className="empty">
          <h2>Nenhum download na fila</h2>
          <p>Cole um link http, https ou magnet acima. O Velocity identifica o arquivo antes de começar e escolhe sozinho quantas conexões usar.</p>
        </div>
      ) : (
        <div className="split">
          <Queue items={items} />
          {selected ? <Detail item={selected} /> : <div className="detail-empty">Escolha um download para ver os detalhes.</div>}
        </div>
      )}
    </div>
  );
}

function Captures() {
  const state = useStore();
  if (state.captures.length === 0) return null;
  return (
    <ul className="captures" aria-label="Downloads capturados do navegador">
      {state.captures.map((capture) => (
        <li key={capture.id}>
          <div className="preview-main">
            <strong className="ellipsis">{capture.name}</strong>
            <span className="muted ellipsis">Do navegador: {capture.display}</span>
          </div>
          <button type="button" className="btn primary" onClick={() => void window.velocity.captureAccept(capture.id).then(() => store.dropCapture(capture.id))}>Baixar</button>
          <button type="button" className="btn" onClick={() => void window.velocity.captureReject(capture.id).then(() => store.dropCapture(capture.id))}>Ignorar</button>
        </li>
      ))}
    </ul>
  );
}

function LinkBar() {
  const state = useStore();
  const [text, setText] = useState('');
  const [previews, setPreviews] = useState<InspectResult[]>([]);
  const [inspecting, setInspecting] = useState(false);
  const [error, setError] = useState('');
  const [clip, setClip] = useState('');
  const [more, setMore] = useState(false);
  const [mirrors, setMirrors] = useState(false);
  const [checksum, setChecksum] = useState('');
  const [user, setUser] = useState('');
  const [password, setPassword] = useState('');
  const [priority, setPriority] = useState(1);
  const [folder, setFolder] = useState('');
  const [adding, setAdding] = useState(false);
  const dismissedClip = useRef('');
  const inspectRun = useRef(0);

  const links = useMemo(() => extractLinks(text), [text]);
  const effectiveFolder = folder || state.settings?.folder || '';

  useEffect(() => {
    const check = async () => {
      const value = await window.velocity.readClipboard();
      const found = extractLinks(value);
      const joined = found.join('\n');
      if (found.length > 0 && joined !== dismissedClip.current && joined !== text) setClip(joined);
      else setClip('');
    };
    void check();
    return window.velocity.onFocus(() => void check());
  }, [text]);

  useEffect(() => {
    if (links.length === 0) {
      setPreviews([]);
      setInspecting(false);
      return;
    }
    const run = ++inspectRun.current;
    setInspecting(true);
    const timer = setTimeout(() => {
      window.velocity
        .call<InspectResult[]>('inspect', { urls: links })
        .then((result) => {
          if (run === inspectRun.current) setPreviews(result);
        })
        .catch((failure: Error) => {
          if (run === inspectRun.current) setError(failure.message);
        })
        .finally(() => {
          if (run === inspectRun.current) setInspecting(false);
        });
    }, 350);
    return () => clearTimeout(timer);
  }, [links]);

  const paste = async () => {
    const value = await window.velocity.readClipboard();
    if (extractLinks(value).length === 0) {
      setError('A área de transferência não tem nenhum link.');
      return;
    }
    setError('');
    dismissedClip.current = extractLinks(value).join('\n');
    setClip('');
    setText(value.trim());
  };

  const reset = () => {
    setText('');
    setChecksum('');
    setUser('');
    setPassword('');
    setMirrors(false);
    setPreviews([]);
  };

  const add = async (urls: string[], kind?: string) => {
    setAdding(true);
    setError('');
    try {
      await window.velocity.call('add', {
        urls,
        kind,
        mirrors: mirrors && urls.length > 1,
        checksum,
        user,
        password,
        priority,
        folder: effectiveFolder,
      });
      reset();
    } catch (failure) {
      setError((failure as Error).message);
    } finally {
      setAdding(false);
    }
  };

  const openTorrent = async () => {
    const file = await window.velocity.pickTorrent();
    if (!file) return;
    try {
      await window.velocity.call('add', { urls: [], torrentFile: file, folder: effectiveFolder, priority });
    } catch (failure) {
      setError((failure as Error).message);
    }
  };

  const pickFolder = async () => {
    const chosen = await window.velocity.pickFolder();
    if (chosen) setFolder(chosen);
  };

  const htmlOnly = previews.length === 1 && previews[0].ok && previews[0].html;

  return (
    <section className="linkbar" aria-label="Adicionar download">
      <div className="linkbar-row">
        <label className="sr" htmlFor="links">Links para baixar</label>
        <textarea
          id="links"
          rows={Math.min(4, Math.max(1, text.split('\n').length))}
          placeholder="Cole aqui um ou mais links"
          value={text}
          spellCheck={false}
          onChange={(event) => {
            setError('');
            setText(event.target.value);
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey && links.length > 0 && !adding) {
              event.preventDefault();
              void add(links);
            }
          }}
        />
        <button type="button" className="btn" onClick={() => void paste()}>Colar</button>
        <button type="button" className="btn primary" disabled={links.length === 0 || adding} onClick={() => void add(links)}>
          {links.length > 1 && !mirrors ? `Baixar ${links.length} arquivos` : 'Baixar'}
        </button>
      </div>
      {clip && links.length === 0 ? (
        <div className="clip">
          <span className="ellipsis">Link copiado: {extractLinks(clip).map(safeDisplay).join(', ')}</span>
          <button type="button" className="link" onClick={() => { setText(clip); setClip(''); dismissedClip.current = clip; }}>Usar este link</button>
          <button type="button" className="link" onClick={() => { dismissedClip.current = clip; setClip(''); }}>Ignorar</button>
        </div>
      ) : null}
      <div className="linkbar-meta">
        <button type="button" className="link folder" onClick={() => void pickFolder()} title={effectiveFolder}>
          <Icon name="folder" /> <span className="ellipsis">Salvar em {effectiveFolder || 'Downloads'}</span>
        </button>
        <button type="button" className="link" aria-expanded={more} onClick={() => setMore(!more)}>{more ? 'Menos opções' : 'Mais opções'}</button>
        <button type="button" className="link" onClick={() => void openTorrent()}>Abrir arquivo .torrent</button>
      </div>
      {more ? (
        <div className="more">
          <label className="field">
            Prioridade
            <select value={priority} onChange={(event) => setPriority(Number(event.target.value))}>
              {PRIORITY.map((label, index) => <option key={label} value={index}>{label}</option>)}
            </select>
          </label>
          <label className="field wide">
            Checksum (MD5, SHA-1, SHA-256 ou SHA-512)
            <input value={checksum} spellCheck={false} onChange={(event) => setChecksum(event.target.value)} />
          </label>
          <label className="field">
            Usuário
            <input value={user} autoComplete="off" onChange={(event) => setUser(event.target.value)} />
          </label>
          <label className="field">
            Senha
            <input type="password" value={password} autoComplete="off" onChange={(event) => setPassword(event.target.value)} />
          </label>
          <label className="check">
            <input type="checkbox" checked={mirrors} onChange={(event) => setMirrors(event.target.checked)} />
            Os links são mirrors do mesmo arquivo
          </label>
        </div>
      ) : null}
      {error ? <p className="form-error" role="alert">{error}</p> : null}
      {inspecting ? <p className="muted inspecting" role="status">Identificando {links.length > 1 ? 'os arquivos' : 'o arquivo'}…</p> : null}
      {!inspecting && previews.length > 0 ? (
        <ul className="previews" aria-label="Arquivos identificados">
          {previews.map((preview, index) => (
            <li key={`${preview.input}-${index}`}>
              {preview.ok ? (
                <>
                  <div className="preview-main">
                    <strong className="ellipsis">{preview.name}</strong>
                    <span className="muted ellipsis">{preview.server}</span>
                  </div>
                  <span className="num">{(preview.size ?? -1) >= 0 ? formatBytes(preview.size as number) : 'Tamanho desconhecido'}</span>
                  <span className="tag">
                    {preview.kind === 'torrent'
                      ? preview.toolReady ? 'Torrent, por aria2' : 'Torrent, aria2 indisponível'
                      : preview.ranges ? 'Várias conexões, retoma' : 'Uma conexão, sem retomada'}
                  </span>
                </>
              ) : (
                <>
                  <div className="preview-main">
                    <strong className="ellipsis">{preview.input || 'Link inválido'}</strong>
                    <span className="bad-text">{preview.message}</span>
                  </div>
                </>
              )}
            </li>
          ))}
        </ul>
      ) : null}
      {!inspecting && htmlOnly ? (
        <div className="html-choice" role="status">
          <span>Este link abre uma página, não um arquivo.</span>
          {previews[0].videoReady ? <button type="button" className="btn primary" onClick={() => void add(links, 'video')}>Baixar o vídeo da página</button> : <span className="muted">Instale o yt-dlp para baixar vídeos de páginas.</span>}
          <button type="button" className="btn" onClick={() => void add(links)}>Baixar a página</button>
        </div>
      ) : null}
    </section>
  );
}

function Queue({ items }: { items: Item[] }) {
  const state = useStore();
  const [dragId, setDragId] = useState('');
  const hasDone = items.some((item) => item.status === 'done');

  const onDrop = (event: DragEvent, targetId: string) => {
    event.preventDefault();
    if (!dragId || dragId === targetId) return;
    const active = items.filter((item) => item.status !== 'done').map((item) => item.id);
    const from = active.indexOf(dragId);
    const to = active.indexOf(targetId);
    if (from < 0 || to < 0) return;
    active.splice(to, 0, active.splice(from, 1)[0]);
    store.reorderLocal(active);
    void window.velocity.call('reorder', { ids: active });
    setDragId('');
  };

  return (
    <section className="queue" aria-label="Fila de downloads">
      <ul>
        {items.map((item) => {
          const percent = item.size > 0 ? Math.min(1, item.done / item.size) : 0;
          return (
            <li
              key={item.id}
              className={`row${state.selectedId === item.id ? ' selected' : ''}${dragId === item.id ? ' dragging' : ''}`}
              draggable={item.status !== 'done'}
              onDragStart={() => setDragId(item.id)}
              onDragEnd={() => setDragId('')}
              onDragOver={(event) => event.preventDefault()}
              onDrop={(event) => onDrop(event, item.id)}
            >
              <button type="button" className="row-main" aria-current={state.selectedId === item.id ? 'true' : undefined} onClick={() => store.select(item.id)}>
                <span className="row-top">
                  {item.priority === 0 ? <span className="prio">Alta</span> : item.priority === 2 ? <span className="prio low">Baixa</span> : null}
                  <span className="row-name ellipsis">{item.name}</span>
                </span>
                <span className="bar" aria-hidden="true">
                  <span className={`bar-fill s-${item.status}`} style={{ transform: `scaleX(${item.status === 'done' ? 1 : percent})` }} />
                </span>
                <span className="row-bottom muted">
                  <span className={item.status === 'failed' || item.status === 'needs-link' || item.status === 'needs-auth' ? 'bad-text' : ''}>{statusLine(item)}</span>
                  <span className="num">{item.size > 0 ? `${formatBytes(item.done)} de ${formatBytes(item.size)}` : item.done > 0 ? formatBytes(item.done) : ''}</span>
                  {isActive(item) ? <span className="num speed">{formatSpeed(item.speed)}</span> : null}
                  {isActive(item) ? <span className="num">{formatEta(item.eta)}</span> : null}
                </span>
              </button>
              <div className="row-actions">
                {item.status === 'running' || item.status === 'queued' ? <IconButton icon="pause" label={`Pausar ${item.name}`} onClick={() => void window.velocity.call('pause', { id: item.id })} /> : null}
                {item.status === 'paused' ? <IconButton icon="play" label={`Continuar ${item.name}`} onClick={() => void window.velocity.call('resume', { id: item.id })} /> : null}
                {item.status === 'failed' ? <IconButton icon="retry" label={`Tentar de novo ${item.name}`} onClick={() => void window.velocity.call('resume', { id: item.id })} /> : null}
                {item.status === 'done' ? <IconButton icon="folder" label={`Mostrar ${item.name} na pasta`} onClick={() => void window.velocity.revealPath(item.finalPath)} /> : null}
              </div>
            </li>
          );
        })}
      </ul>
      {hasDone ? <button type="button" className="link clear-done" onClick={() => void window.velocity.call('clearDone')}>Tirar concluídos da lista</button> : null}
    </section>
  );
}

function Detail({ item }: { item: Item }) {
  const state = useStore();
  const speed = splitSpeed(item.speed);
  const series = state.series.get(item.id);
  const running = item.status === 'running';
  const live = running && item.phase === 'downloading';
  const links = item.links ?? [];
  const showLinks = links.length > 0 && running;
  const legend = links.filter((link) => link.connections > 0 || links.length > 1);

  return (
    <section className="detail" aria-label={`Detalhes de ${item.name}`}>
      <header className="detail-head">
        <div className="detail-title">
          <h2 className="ellipsis" title={item.name}>{item.name}</h2>
          <p className="muted ellipsis">
            {item.display}
            {item.mirrors > 0 ? `, mais ${item.mirrors} ${item.mirrors === 1 ? 'mirror' : 'mirrors'}` : ''}
          </p>
        </div>
        <Actions item={item} />
      </header>

      <div className="kpis">
        <div className="kpi hero-kpi">
          <span className="kpi-label">Agora</span>
          <span className="hero-value num">{running ? speed.number : '0'}<small> {running ? speed.unit : 'B/s'}</small></span>
        </div>
        <div className="kpi-rest">
        <Kpi label="Média" value={formatSpeed(item.average)} />
        <Kpi label="Máxima" value={formatSpeed(item.max)} />
        <Kpi label="Baixado" value={item.size > 0 ? formatPercent(item.status === 'done' ? item.size : item.done, item.size) : formatBytes(item.done)} unit={item.size > 0 ? `${formatBytes(item.status === 'done' ? item.size : item.done)} de ${formatBytes(item.size)}` : undefined} />
        <Kpi label="Falta" value={running ? formatEta(item.eta) : '—'} />
        <Kpi label="Conexões" value={running ? String(item.connections) : '—'} unit={running && item.target > 0 ? `alvo ${item.target}` : undefined} />
        <Kpi label="Fragmentos" value={running ? String(item.fragments) : '—'} />
        </div>
      </div>

      <Problem item={item} />

      {running && item.bottleneck ? <p className="insight"><strong>Gargalo.</strong> {BOTTLENECK[item.bottleneck]}</p> : null}
      {item.note ? <p className="insight">{item.note}</p> : null}
      {item.status === 'running' && !item.resumable ? <p className="insight">Este servidor não aceita pedidos parciais. O download usa uma conexão só e recomeça do zero se for interrompido.</p> : null}
      {item.status === 'done' ? <DoneSummary item={item} /> : null}

      {item.kind === 'http' ? (
        <div className="panel">
          <div className="panel-head">
            <h3>Mapa do arquivo</h3>
            <div className="legend">
              <span><i style={{ background: '#4f8a9e' }} /> Gravado</span>
              {live ? legend.map((link) => <span key={link.id}><i style={{ background: store.linkColor(link.id) }} /> {link.name}</span>) : null}
              <span><i className="pending" /> Falta</span>
            </div>
          </div>
          <FragmentMap item={item} map={state.selectedId === item.id ? state.map : null} />
        </div>
      ) : (
        <p className="insight">{item.kind === 'torrent' ? 'Torrent baixado pelo aria2. Durante o download seu IP fica visível para outros participantes e partes do arquivo são enviadas a eles. Ao concluir, o envio para.' : 'Vídeo baixado pelo yt-dlp, que escolhe o formato e usa o ffmpeg para juntar áudio e vídeo quando disponível.'}</p>
      )}

      <div className="panel">
        <div className="panel-head">
          <h3>Velocidade</h3>
          {series && series.links.size > 1 ? (
            <div className="legend">
              <span><i style={{ background: '#e6ecef' }} /> Total</span>
              {links.map((link) => <span key={link.id}><i style={{ background: store.linkColor(link.id) }} /> {link.name}</span>)}
            </div>
          ) : null}
        </div>
        <SpeedChart series={series} version={state.getVersion()} label={`Velocidade atual ${formatSpeed(running ? item.speed : 0)}, máxima ${formatSpeed(item.max)}, média ${formatSpeed(item.average)}.`} />
      </div>

      {showLinks ? (
        <div className="panel">
          <div className="panel-head"><h3>Por rede</h3></div>
          <table className="table compact">
            <thead>
              <tr><th scope="col">Rede</th><th scope="col" className="r">Velocidade</th><th scope="col" className="r">Conexões</th></tr>
            </thead>
            <tbody>
              {links.map((link) => (
                <tr key={link.id}>
                  <td><i className="dot" style={{ background: store.linkColor(link.id) }} /> {link.name}{link.noGain ? <span className="muted"> sem ganho medido, fora de uso</span> : null}</td>
                  <td className="r num">{formatSpeed(link.speed)}</td>
                  <td className="r num">{link.connections}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </section>
  );
}

function DoneSummary({ item }: { item: Item }) {
  const parts: string[] = [];
  if (item.verified) parts.push('Os dados gravados foram relidos do disco e conferem com o que foi recebido.');
  if (item.checksumState === 'ok') parts.push('O checksum do arquivo confere.');
  if (!item.verified && item.kind === 'http') parts.push('A conferência dos dados estava desligada.');
  if (item.kind === 'http' && item.checksumState !== 'ok') parts.push('Sem checksum informado, não há como comparar com o original do publicador.');
  return parts.length > 0 ? <p className="insight ok">{parts.join(' ')}</p> : null;
}

function Actions({ item }: { item: Item }) {
  const [confirm, setConfirm] = useState(false);
  useEffect(() => setConfirm(false), [item.id]);
  const call = (command: string, args: object = {}) => void window.velocity.call(command, { id: item.id, ...args });
  return (
    <div className="actions">
      {item.status === 'running' || item.status === 'queued' ? <button type="button" className="btn" onClick={() => call('pause')}><Icon name="pause" /> Pausar</button> : null}
      {item.status === 'paused' ? <button type="button" className="btn primary" onClick={() => call('resume')}><Icon name="play" /> Continuar</button> : null}
      {item.status === 'failed' ? <button type="button" className="btn primary" onClick={() => call('resume')}><Icon name="retry" /> Tentar de novo</button> : null}
      {item.status !== 'done' ? (
        <>
          <label className="sr" htmlFor={`prio-${item.id}`}>Prioridade</label>
          <select id={`prio-${item.id}`} className="select" value={item.priority} onChange={(event) => call('priority', { priority: Number(event.target.value) })}>
            {PRIORITY.map((label, index) => <option key={label} value={index}>Prioridade {label.toLowerCase()}</option>)}
          </select>
          <IconButton icon="up" label="Subir na fila" onClick={() => call('move', { direction: 'up' })} />
          <IconButton icon="down" label="Descer na fila" onClick={() => call('move', { direction: 'down' })} />
          {confirm ? (
            <>
              <button type="button" className="btn danger" onClick={() => call('cancel')}>Cancelar e apagar o parcial</button>
              <button type="button" className="btn" onClick={() => setConfirm(false)}>Manter</button>
            </>
          ) : (
            <button type="button" className="btn" onClick={() => setConfirm(true)}><Icon name="close" /> Cancelar</button>
          )}
        </>
      ) : (
        <>
          <button type="button" className="btn" onClick={() => void window.velocity.revealPath(item.finalPath)}><Icon name="folder" /> Mostrar na pasta</button>
          <button type="button" className="btn" onClick={() => call('remove')}>Tirar da lista</button>
        </>
      )}
    </div>
  );
}

function Problem({ item }: { item: Item }) {
  const [url, setUrl] = useState('');
  const [user, setUser] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    setUrl('');
    setUser('');
    setPassword('');
    setError('');
  }, [item.id]);

  if (item.status === 'failed') return <div className="problem" role="alert"><strong>Não foi possível concluir.</strong> {item.errorMessage}</div>;
  if (item.status === 'needs-link') {
    return (
      <form
        className="problem"
        onSubmit={(event) => {
          event.preventDefault();
          setBusy(true);
          setError('');
          window.velocity
            .call('updateLink', { id: item.id, url })
            .catch((failure: Error) => setError(failure.message))
            .finally(() => setBusy(false));
        }}
      >
        <strong>O link parou de funcionar.</strong> {item.errorMessage} O que já foi baixado continua guardado.
        <div className="problem-row">
          <label className="sr" htmlFor="new-link">Link novo</label>
          <input id="new-link" value={url} placeholder="Cole o link novo do mesmo arquivo" spellCheck={false} onChange={(event) => setUrl(event.target.value)} />
          <button type="submit" className="btn primary" disabled={busy || !url}>Continuar com este link</button>
        </div>
        {error ? <p className="form-error" role="alert">{error}</p> : null}
      </form>
    );
  }
  if (item.status === 'needs-auth') {
    return (
      <form
        className="problem"
        onSubmit={(event) => {
          event.preventDefault();
          void window.velocity.call('setAuth', { id: item.id, user, password }).catch((failure: Error) => setError(failure.message));
          setPassword('');
        }}
      >
        <strong>O servidor pediu login.</strong> A senha fica protegida pelo Windows neste computador e só é enviada a este servidor.
        <div className="problem-row">
          <label className="sr" htmlFor="auth-user">Usuário</label>
          <input id="auth-user" value={user} placeholder="Usuário" autoComplete="off" onChange={(event) => setUser(event.target.value)} />
          <label className="sr" htmlFor="auth-pass">Senha</label>
          <input id="auth-pass" type="password" value={password} placeholder="Senha" autoComplete="off" onChange={(event) => setPassword(event.target.value)} />
          <button type="submit" className="btn primary" disabled={!user}>Entrar e continuar</button>
        </div>
        {error ? <p className="form-error" role="alert">{error}</p> : null}
      </form>
    );
  }
  return null;
}
