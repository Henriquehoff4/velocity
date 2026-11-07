import type { ReactNode } from 'react';

export function Logo() {
  return (
    <svg className="logo" viewBox="0 0 32 32" width="24" height="24" aria-hidden="true">
      <path d="M5 6 16 27 27 6" fill="none" stroke="#5cc8e6" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M11 6 16 17" fill="none" stroke="#e8a23b" strokeWidth="2.2" strokeLinecap="round" />
      <path d="M21 6 16 17" fill="none" stroke="#e5728a" strokeWidth="2.2" strokeLinecap="round" />
    </svg>
  );
}

const PATHS: Record<string, ReactNode> = {
  pause: <path d="M6 4v12M14 4v12" />,
  play: <path d="M6 4l10 6-10 6z" />,
  close: <path d="M5 5l10 10M15 5L5 15" />,
  up: <path d="M10 16V5M5 10l5-5 5 5" />,
  down: <path d="M10 4v11M5 10l5 5 5-5" />,
  retry: <path d="M15.5 7A6.5 6.5 0 1 0 16.5 11M16 3v4h-4" />,
  folder: <path d="M3 6h5l2 2h7v8H3z" />,
  grip: <path d="M7 5h.01M13 5h.01M7 10h.01M13 10h.01M7 15h.01M13 15h.01" />,
};

export function Icon({ name }: { name: keyof typeof PATHS | string }) {
  return (
    <svg className="icon" viewBox="0 0 20 20" width="16" height="16" fill="none" stroke="currentColor" strokeWidth={name === 'grip' ? 2.4 : 1.7} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {PATHS[name]}
    </svg>
  );
}

export function IconButton({ icon, label, onClick, disabled }: { icon: string; label: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button type="button" className="icon-btn" aria-label={label} title={label} disabled={disabled} onClick={onClick}>
      <Icon name={icon} />
    </button>
  );
}

export function Kpi({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <div className="kpi">
      <span className="kpi-label">{label}</span>
      <span className="kpi-value num">
        {value}
        {unit ? <small> {unit}</small> : null}
      </span>
    </div>
  );
}
