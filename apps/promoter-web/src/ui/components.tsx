import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react';
import { useId } from 'react';

export function Button({
  kind = 'primary',
  busy,
  children,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { kind?: 'primary' | 'secondary' | 'danger'; busy?: boolean }) {
  return (
    <button type="button" className={`button ${kind}`} disabled={busy || props.disabled} aria-busy={busy || undefined} {...props}>
      {busy ? 'Working…' : children}
    </button>
  );
}

export function Field({ label, error, hint, ...input }: InputHTMLAttributes<HTMLInputElement> & { label: string; error?: string; hint?: string }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} aria-invalid={error ? true : undefined} aria-describedby={hint ? `${id}-hint` : undefined} {...input} />
      {hint && <small id={`${id}-hint`}>{hint}</small>}
      {error && <span className="error" role="alert">{error}</span>}
    </div>
  );
}

export function Select({
  label, hideLabel, children, ...select
}: SelectHTMLAttributes<HTMLSelectElement> & { label: string; hideLabel?: boolean; children: ReactNode }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id} className={hideLabel ? 'visually-hidden' : undefined}>{label}</label>
      <select id={id} {...select}>
        {children}
      </select>
    </div>
  );
}

export function ErrorText({ children }: { children?: ReactNode }) {
  return children ? (
    <p className="error" role="alert">
      {children}
    </p>
  ) : null;
}

export function Loading({ label = 'Loading' }: { label?: string }) {
  return (
    <p className="muted" role="status">
      {label}…
    </p>
  );
}

// The same word means different things per kind: an allocation is "selected" once its draw has run; a fan is
// "selected" when they got a ticket.
const STATUS_LABELS: Record<'allocation' | 'registration' | 'reservation' | 'draw', Record<string, string>> = {
  allocation: {
    draft: 'Draft', upcoming: 'Upcoming', open: 'Registration open', closed: 'Registration closed', selected: 'Drawn',
    cancelled: 'Cancelled',
  },
  registration: { registered: 'Registered', withdrawn: 'Withdrawn', selected: 'Selected', not_selected: 'Not selected', returned: 'Returned' },
  reservation: { held: 'Held', delivered: 'Delivered', returned: 'Returned', cancelled: 'Cancelled' },
  draw: { not_run: 'Not run', queued: 'Queued', running: 'Running', failed: 'Failed', done: 'Done' },
};

export function Status({ value, kind }: { value: string; kind: keyof typeof STATUS_LABELS }) {
  return <span className={`status status-${kind}-${value}`}>{STATUS_LABELS[kind][value] ?? value}</span>;
}

export function Card({ title, actions, children }: { title?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="card">
      {(title || actions) && (
        <header className="card-header">
          {title && <h2>{title}</h2>}
          {actions && <div className="actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}
