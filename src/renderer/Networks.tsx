import { useState } from 'react';
import { formatSpeed } from '../shared/format';
import type { NetLinkInfo } from '../shared/types';
import { store, useStore } from './store';

const KIND: Record<string, string> = { ethernet: 'Cabo', wifi: 'Wi-Fi', mobile: 'Rede móvel', vpn: 'VPN', virtual: 'Adaptador virtual', test: 'Teste', other: 'Outra' };

function independence(link: NetLinkInfo, links: NetLinkInfo[]): string {
  if (link.virtual && link.kind !== 'test') return 'Virtual: usa outra rede por baixo';
  if (link.noGain) return 'Medido: sem ganho ao somar';
  const peers = links.filter((other) => other.group === link.group && other.id !== link.id && !other.virtual);
  if (peers.length > 0) return `Mesmo acesso que ${peers[0].name}`;
  return link.publicIp ? 'Independente (IP público próprio)' : 'Parece independente';
}

export function NetworksView() {
  const state = useStore();
  const { links, independent, combined } = state.net;
  const [checking, setChecking] = useState(false);
  const [checkError, setCheckError] = useState('');
  const [confirming, setConfirming] = useState(false);
  const usable = independent >= 2;

  const check = async () => {
    setConfirming(false);
    setChecking(true);
    setCheckError('');
    try {
      await window.velocity.call('net.checkPublic');
      store.net = await window.velocity.call('net.list');
    } catch (failure) {
      setCheckError((failure as Error).message);
    } finally {
      setChecking(false);
    }
  };

  return (
    <div className="page">
      <section className="net-head">
        <div>
          <span className="kpi-label">Velocidade somada do Velocity agora</span>
          <div className="hero-value num">{formatSpeed(combined)}</div>
        </div>
        <p className="statement">
          {links.length === 0
            ? 'Nenhuma rede com acesso à internet foi encontrada.'
            : usable
              ? `${independent} acessos independentes encontrados. Downloads com várias conexões distribuem partes entre eles, na proporção da velocidade medida.`
              : 'Só um acesso à internet foi encontrado, então não há o que somar. Conecte uma segunda rede de verdade, como um celular roteando, para usar as duas.'}
        </p>
      </section>

      <section className="panel" aria-label="Redes encontradas">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Rede</th>
              <th scope="col">Tipo</th>
              <th scope="col">Endereço</th>
              <th scope="col">Situação</th>
              <th scope="col" className="r">Capacidade do adaptador</th>
              <th scope="col" className="r">Velocity</th>
              <th scope="col" className="r">Todo o Windows</th>
              <th scope="col" className="r">Conexões</th>
            </tr>
          </thead>
          <tbody>
            {links.map((link) => (
              <tr key={link.id}>
                <td><i className="dot" style={{ background: store.linkColor(link.id) }} /> {link.name}</td>
                <td>{KIND[link.kind] ?? 'Outra'}</td>
                <td className="mono">{link.ipv4 || link.ipv6}{link.publicIp ? <span className="muted"> público {link.publicIp}</span> : null}</td>
                <td>{independence(link, links)}</td>
                <td className="r num">{link.nominal > 0 ? `${Math.round((link.nominal * 8) / 1e6).toLocaleString('pt-BR')} Mbit/s` : '—'}</td>
                <td className="r num">{formatSpeed(link.speed)}</td>
                <td className="r num">{formatSpeed(link.osSpeed)}</td>
                <td className="r num">{link.connections}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {links.length === 0 ? <p className="muted pad">Verifique o cabo ou o Wi-Fi. A lista atualiza sozinha.</p> : null}
      </section>

      <section className="two">
        <div className="panel prose">
          <h3>Como o Velocity usa duas redes</h3>
          <p>Cada conexão é presa a uma rede. Partes diferentes do arquivo chegam por caminhos diferentes, e a rede mais rápida acaba pegando mais trabalho. Se uma cair, as outras terminam o que faltava.</p>
          <p>Nenhuma rota nem configuração do Windows é alterada. Tudo vale só para as conexões do próprio Velocity.</p>
          <p>Só funciona quando o servidor aceita pedidos parciais. Uma única conexão não é dividida entre redes.</p>
          <p>Duas redes no mesmo roteador dividem a mesma internet. O Velocity usa só uma delas e, se a soma não aumentar a velocidade medida, tira a segunda de uso.</p>
        </div>
        <div className="panel prose">
          <h3>Conferir se as redes são mesmo independentes</h3>
          <p>Redes atrás do mesmo provedor nem sempre dá para distinguir localmente. Esta conferência pergunta o IP público de cada rede ao serviço externo api.ipify.org. Esse serviço recebe seu endereço IP. Só roda quando você pede.</p>
          {confirming ? (
            <div className="actions">
              <button type="button" className="btn primary" onClick={() => void check()}>Consultar api.ipify.org</button>
              <button type="button" className="btn" onClick={() => setConfirming(false)}>Agora não</button>
            </div>
          ) : (
            <button type="button" className="btn" disabled={checking} onClick={() => setConfirming(true)}>{checking ? 'Conferindo…' : 'Conferir IP público de cada rede'}</button>
          )}
          {checkError ? <p className="form-error" role="alert">{checkError}</p> : null}
          <h3 className="after">E bonding de verdade?</h3>
          <p>Somar redes em uma única conexão, como em chamadas ou em servidores sem pedidos parciais, exige um servidor do outro lado: uma VPN de bonding, como Speedify ou OpenMPTCPRouter. O Velocity não faz isso. Se você usar um desses serviços, ele aparece aqui como uma rede comum e pode ser escolhido no modo Avançado.</p>
        </div>
      </section>
    </div>
  );
}
