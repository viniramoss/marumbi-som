import {
  forwardRef,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type MouseEventHandler,
  type ReactNode,
  type SelectHTMLAttributes,
} from 'react';
import { LoaderCircle } from 'lucide-react';
import type { Tone } from '../lib/status';

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';

const variants: Record<Variant, string> = {
  primary: 'bg-brand-700 text-white hover:bg-brand-800 disabled:bg-brand-700/50 shadow-sm',
  secondary: 'bg-white text-stone-800 ring-1 ring-stone-300 hover:bg-stone-50 disabled:text-stone-400 shadow-sm',
  ghost: 'text-stone-700 hover:bg-stone-200/70 disabled:text-stone-400',
  danger: 'bg-white text-red-700 ring-1 ring-red-300 hover:bg-red-50 disabled:text-red-300',
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: 'sm' | 'md' | 'lg';
  busy?: boolean;
  icon?: ReactNode;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    variant = 'secondary',
    size = 'md',
    busy = false,
    icon,
    children,
    className = '',
    disabled,
    type = 'button',
    ...rest
  },
  ref,
) {
  const sizes = { sm: 'h-8 px-2.5 text-sm gap-1.5', md: 'h-9 px-3.5 text-sm gap-2', lg: 'h-11 px-5 text-base gap-2' };
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || busy}
      className={`inline-flex shrink-0 items-center justify-center rounded-lg font-medium transition-colors ${sizes[size]} ${variants[variant]} ${className}`}
      {...rest}
    >
      {busy ? <LoaderCircle className="size-4 animate-spin" aria-hidden /> : icon}
      {children}
    </button>
  );
});

export function IconButton({
  label,
  children,
  className = '',
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      className={`inline-flex size-9 shrink-0 items-center justify-center rounded-lg text-stone-600 transition-colors hover:bg-stone-200/70 hover:text-stone-900 disabled:text-stone-300 disabled:hover:bg-transparent ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}

export function Card({
  children,
  className = '',
  onClick,
}: {
  children: ReactNode;
  className?: string;
  /** Card inteiro clicável (abre a tela da loja, por exemplo); botões e links dentro seguem com a ação deles. */
  onClick?: MouseEventHandler<HTMLElement>;
}) {
  return (
    <section
      onClick={onClick}
      className={`rounded-xl bg-white shadow-sm ring-1 ring-stone-200 ${
        onClick ? 'cursor-pointer transition-shadow hover:shadow-md hover:ring-brand-200' : ''
      } ${className}`}
    >
      {children}
    </section>
  );
}

export function CardHeader({
  title,
  actions,
  subtitle,
}: {
  title: ReactNode;
  actions?: ReactNode;
  subtitle?: ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-stone-100 px-5 py-3.5">
      <div className="min-w-0">
        <h2 className="text-base font-semibold text-stone-900">{title}</h2>
        {subtitle && <p className="mt-0.5 text-sm text-stone-500">{subtitle}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

const tones: Record<Tone, string> = {
  green: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
  amber: 'bg-amber-50 text-amber-800 ring-amber-200',
  red: 'bg-red-50 text-red-800 ring-red-200',
  gray: 'bg-stone-100 text-stone-700 ring-stone-200',
  blue: 'bg-sky-50 text-sky-800 ring-sky-200',
};

const dots: Record<Tone, string> = {
  green: 'bg-emerald-500',
  amber: 'bg-amber-500',
  red: 'bg-red-500',
  gray: 'bg-stone-400',
  blue: 'bg-sky-500',
};

export function Badge({ tone = 'gray', children, dot = false }: { tone?: Tone; children: ReactNode; dot?: boolean }) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${tones[tone]}`}
    >
      {dot && <span className={`size-1.5 rounded-full ${dots[tone]}`} aria-hidden />}
      {children}
    </span>
  );
}

export function Spinner({ label = 'Carregando…' }: { label?: string }) {
  return (
    <div role="status" className="flex items-center justify-center gap-2 py-10 text-sm text-stone-500">
      <LoaderCircle className="size-5 animate-spin" aria-hidden />
      {label}
    </div>
  );
}

export function EmptyState({ icon, title, children }: { icon?: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-12 text-center">
      {icon && <div className="text-stone-400">{icon}</div>}
      <p className="font-medium text-stone-800">{title}</p>
      {children && <div className="max-w-md text-sm text-stone-500">{children}</div>}
    </div>
  );
}

export function ErrorNote({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800 ring-1 ring-red-200">
      {children}
    </p>
  );
}

export function PageHeader({
  title,
  description,
  actions,
}: {
  title: string;
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-2xl font-semibold tracking-tight text-stone-900">{title}</h1>
        {description && <p className="mt-1 text-sm text-stone-500">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-stone-700">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs text-stone-500">{hint}</span>}
    </label>
  );
}

const inputClass =
  'block w-full rounded-lg border-0 bg-white px-3 py-2 text-sm text-stone-900 shadow-sm ring-1 ring-stone-300 placeholder:text-stone-400 focus:ring-2 focus:ring-brand-600 focus:outline-none disabled:bg-stone-50 disabled:text-stone-500';

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(function Input(
  { className = '', ...rest },
  ref,
) {
  return <input ref={ref} className={`${inputClass} ${className}`} {...rest} />;
});

export function Select({ className = '', children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select className={`${inputClass} pr-8 ${className}`} {...rest}>
      {children}
    </select>
  );
}

/** Barra de andamento; sem `value` fica indeterminada (algo em andamento sem porcentagem, ex.: processando no Hub). */
export function ProgressBar({ value, max = 100, label }: { value?: number; max?: number; label?: string }) {
  if (value === undefined) {
    return (
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-stone-200" role="progressbar" aria-label={label}>
        <div className="h-full w-2/5 animate-indeterminate rounded-full bg-brand-600" />
      </div>
    );
  }
  const percent = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  return (
    <div
      className="h-1.5 w-full overflow-hidden rounded-full bg-stone-200"
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={Math.round(value)}
    >
      <div
        className="h-full rounded-full bg-brand-600 transition-[width] duration-1000 ease-linear"
        style={{ width: `${percent}%` }}
      />
    </div>
  );
}

export function Tabs<T extends string>({
  tabs,
  value,
  onChange,
}: {
  tabs: readonly { id: T; label: string }[];
  value: T;
  onChange: (id: T) => void;
}) {
  return (
    <div role="tablist" className="flex gap-1 overflow-x-auto border-b border-stone-200">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          role="tab"
          type="button"
          aria-selected={tab.id === value}
          onClick={() => onChange(tab.id)}
          className={`-mb-px shrink-0 border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
            tab.id === value
              ? 'border-brand-700 text-brand-800'
              : 'border-transparent text-stone-500 hover:text-stone-800'
          }`}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}

export function SoonTag({ stage }: { stage: number }) {
  return (
    <span className="rounded bg-stone-200 px-1.5 py-0.5 text-[10px] font-semibold tracking-wide text-stone-600 uppercase">
      Etapa {stage}
    </span>
  );
}
