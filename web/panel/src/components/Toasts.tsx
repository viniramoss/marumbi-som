import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { CircleAlert, CircleCheck, Info, X } from 'lucide-react';

type ToastKind = 'success' | 'error' | 'info';

interface Toast {
  id: number;
  kind: ToastKind;
  text: string;
}

interface ToastApi {
  success: (text: string) => void;
  error: (text: string) => void;
  info: (text: string) => void;
}

const ToastContext = createContext<ToastApi | null>(null);

let nextId = 1;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: number) => setToasts((list) => list.filter((t) => t.id !== id)), []);
  const push = useCallback(
    (kind: ToastKind, text: string) => {
      const id = nextId++;
      setToasts((list) => [...list.slice(-3), { id, kind, text }]);
      setTimeout(() => dismiss(id), kind === 'error' ? 7000 : 3500);
    },
    [dismiss],
  );
  const value = useMemo<ToastApi>(
    () => ({
      success: (text) => push('success', text),
      error: (text) => push('error', text),
      info: (text) => push('info', text),
    }),
    [push],
  );

  const icons = {
    success: <CircleCheck className="size-5 text-emerald-600" aria-hidden />,
    error: <CircleAlert className="size-5 text-red-600" aria-hidden />,
    info: <Info className="size-5 text-sky-600" aria-hidden />,
  };

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div
        aria-live="polite"
        className="pointer-events-none fixed inset-x-0 bottom-20 z-[60] flex flex-col items-center gap-2 px-4 md:bottom-6 md:items-end"
      >
        {toasts.map((toast) => (
          <div
            key={toast.id}
            role={toast.kind === 'error' ? 'alert' : 'status'}
            className="pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-xl bg-white px-4 py-3 text-sm shadow-lg ring-1 ring-stone-200"
          >
            {icons[toast.kind]}
            <p className="flex-1 text-stone-800">{toast.text}</p>
            <button
              type="button"
              aria-label="Fechar aviso"
              onClick={() => dismiss(toast.id)}
              className="text-stone-400 hover:text-stone-700"
            >
              <X className="size-4" />
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): ToastApi {
  const context = useContext(ToastContext);
  if (!context) throw new Error('useToast fora do ToastProvider');
  return context;
}
