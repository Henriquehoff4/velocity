import { useEffect, useState } from 'react';
import { formatBytes, formatDate, formatSpeed } from '../shared/format';
import type { HistoryEntry } from '../shared/types';
import { IconButton } from './ui';

const OUTCOME: Record<string, string> = { done: 'Concluído', failed: 'Falhou', cancelled: 'Cancelado' };

export function HistoryView() {
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  const [error, setError] = useState('');
  const [filter, setFilter] = useState('');
  const [confirm, setConfirm] = useState(false);

  const load = () => {
    setError('');
    window.velocity.call<HistoryEntry[]>('history.list', { limit: 1000 }).then(setEntries).catch((failure: Error) => setError(failure.message));
  };
  useEffect(load, []);

  if (error) {
    return (
      <div className="page">
        <p className="form-error" role="alert">Não foi possível abrir o histórico: {error}</p>
        <button type="button" className="btn" onClick={load}>Tentar de novo</button>
      </div>
    );
  }
  if (entries === null) return <div className="page"><p className="muted" role="status">Abrindo o histórico…</p></div>;
  const query = filter.trim().toLowerCase();
  const shown = entries.filter((entry) => !query || entry.name.toLowerCase().includes(query) || entry.server.toLowerCase().includes(query));

  return (
    <div className="page">
      {entries.length === 0 ? (
        <div className="empty">
          <h2>O histórico está vazio</h2>
          <p>Downloads concluídos, com falha ou cancelados aparecem aqui. O endereço completo não é guardado, só o servidor.</p>
        </div>
      ) : (
        <>
          <div className="toolbar">
            <label className="sr" htmlFor="history-filter">Filtrar histórico</label>
            <input id="history-filter" placeholder="Filtrar por nome ou servidor" value={filter} onChange={(event) => setFilter(event.target.value)} />
            <span className="muted">{shown.length} de {entries.length}</span>
            <span className="spacer" />
            {confirm ? (
              <>
                <button type="button" className="btn danger" onClick={() => void window.velocity.call('history.clear').then(() => { setEntries([]); setConfirm(false); })}>Apagar o histórico</button>
                <button type="button" className="btn" onClick={() => setConfirm(false)}>Manter</button>
              </>
            ) : (
              <button type="button" className="btn" onClick={() => setConfirm(true)}>Limpar histórico</button>
            )}
          </div>
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Arquivo</th>
                <th scope="col">Servidor</th>
                <th scope="col">Resultado</th>
                <th scope="col" className="r">Tamanho</th>
                <th scope="col" className="r">Média</th>
                <th scope="col" className="r">Máxima</th>
                <th scope="col">Quando</th>
                <th scope="col"><span className="sr">Ações</span></th>
              </tr>
            </thead>
            <tbody>
              {shown.map((entry, index) => (
                <tr key={`${entry.id}-${index}`}>
                  <td className="cell-name"><span className="ellipsis" title={entry.name}>{entry.name}</span></td>
                  <td className="muted">{entry.server}</td>
                  <td className={entry.outcome === 'failed' ? 'bad-text' : ''} title={entry.error}>
                    {OUTCOME[entry.outcome] ?? entry.outcome}
                    {entry.outcome === 'done' && entry.checksum === 'ok' ? ', checksum confere' : ''}
                  </td>
                  <td className="r num">{entry.size >= 0 ? formatBytes(entry.size) : '—'}</td>
                  <td className="r num">{entry.outcome === 'done' ? formatSpeed(entry.average) : '—'}</td>
                  <td className="r num">{entry.outcome === 'done' ? formatSpeed(entry.max) : '—'}</td>
                  <td className="num">{formatDate(entry.at)}</td>
                  <td className="r">{entry.path ? <IconButton icon="folder" label={`Mostrar ${entry.name} na pasta`} onClick={() => void window.velocity.revealPath(entry.path)} /> : null}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}
