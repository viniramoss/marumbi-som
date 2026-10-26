import { NavLink, Outlet, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { House, Library, ListMusic, LogOut, Megaphone, Settings, Store } from 'lucide-react';
import type { ReactNode } from 'react';
import { api } from '../api/client';
import { keys, useSession } from '../api/queries';
import { useLiveState } from '../hooks/useLive';
import { HubUpdatedBanner } from './HubUpdatedBanner';

interface NavItem {
  to: string;
  label: string;
  icon: ReactNode;
  mobile?: boolean;
}

const nav: NavItem[] = [
  { to: '/', label: 'Início', icon: <House className="size-5" />, mobile: true },
  { to: '/lojas', label: 'Lojas', icon: <Store className="size-5" />, mobile: true },
  { to: '/playlists', label: 'Playlists', icon: <ListMusic className="size-5" />, mobile: true },
  { to: '/biblioteca', label: 'Biblioteca', icon: <Library className="size-5" />, mobile: true },
  // Fora da barra do celular (cinco itens cabem; o painel é pensado primeiro para o computador).
  { to: '/ofertas', label: 'Ofertas', icon: <Megaphone className="size-5" /> },
  { to: '/configuracoes', label: 'Configurações', icon: <Settings className="size-5" />, mobile: true },
];

function LiveIndicator() {
  const state = useLiveState();
  const look = {
    online: { dot: 'bg-emerald-500', text: 'Ao vivo' },
    connecting: { dot: 'bg-amber-400 animate-pulse', text: 'Conectando…' },
    offline: { dot: 'bg-red-500 animate-pulse', text: 'Reconectando…' },
  }[state];
  return (
    <span
      className="inline-flex items-center gap-2 text-xs font-medium text-stone-300"
      title="Atualização em tempo real"
    >
      <span className={`size-2 rounded-full ${look.dot}`} aria-hidden />
      {look.text}
    </span>
  );
}

export function Layout() {
  const session = useSession();
  const client = useQueryClient();
  const navigate = useNavigate();

  const logout = async () => {
    await api.POST('/api/auth/logout');
    client.setQueryData(keys.session, null);
    client.clear();
    void navigate('/entrar', { replace: true });
  };

  return (
    <div className="min-h-dvh md:flex">
      <aside className="hidden w-60 shrink-0 flex-col bg-stone-900 text-stone-200 md:fixed md:inset-y-0 md:flex">
        <div className="flex items-center gap-3 px-5 py-5">
          <img src="/favicon.svg" alt="" className="size-9" />
          <div>
            <p className="text-sm leading-tight font-semibold text-white">Marumbi Som</p>
            <p className="text-xs text-stone-400">Casa de Carnes Marumbi</p>
          </div>
        </div>
        <nav className="flex-1 space-y-0.5 px-3" aria-label="Principal">
          {nav.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === '/'}
              className={({ isActive }) =>
                `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
                  isActive ? 'bg-white/10 text-white' : 'text-stone-300 hover:bg-white/5 hover:text-white'
                }`
              }
            >
              {item.icon}
              <span className="flex-1">{item.label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="space-y-3 border-t border-white/10 px-5 py-4">
          <LiveIndicator />
          <div className="flex items-center justify-between gap-2">
            <span className="truncate text-sm text-stone-400">{session.data?.username}</span>
            <button
              type="button"
              onClick={() => void logout()}
              className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-stone-300 hover:bg-white/10 hover:text-white"
            >
              <LogOut className="size-3.5" /> Sair
            </button>
          </div>
        </div>
      </aside>

      <header className="sticky top-0 z-30 flex items-center justify-between bg-stone-900 px-4 py-3 text-white md:hidden">
        <div className="flex items-center gap-2">
          <img src="/favicon.svg" alt="" className="size-7" />
          <span className="text-sm font-semibold">Marumbi Som</span>
        </div>
        <div className="flex items-center gap-3">
          <LiveIndicator />
          <button type="button" aria-label="Sair" onClick={() => void logout()} className="text-stone-300">
            <LogOut className="size-4" />
          </button>
        </div>
      </header>

      <main className="min-w-0 flex-1 px-4 pt-5 pb-24 md:ml-60 md:px-8 md:pt-8 md:pb-10">
        <div className="mx-auto max-w-7xl">
          <HubUpdatedBanner />
          <Outlet />
        </div>
      </main>

      <nav
        aria-label="Principal (celular)"
        className="fixed inset-x-0 bottom-0 z-30 grid grid-cols-5 border-t border-stone-200 bg-white pb-[env(safe-area-inset-bottom)] md:hidden"
      >
        {nav
          .filter((item) => item.mobile)
          .map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === '/'}
              className={({ isActive }) =>
                `flex flex-col items-center gap-0.5 py-2 text-[11px] font-medium ${isActive ? 'text-brand-700' : 'text-stone-500'}`
              }
            >
              {item.icon}
              {item.label === 'Configurações' ? 'Ajustes' : item.label}
            </NavLink>
          ))}
      </nav>
    </div>
  );
}
