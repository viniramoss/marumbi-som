import { Link } from 'react-router';
import { Bluetooth, ChevronRight, RefreshCw, Volume2 } from 'lucide-react';
import type { StoreSummary } from '../../api/types';
import { Badge, Card } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { formatAgo } from '../../lib/format';
import { bluetoothStatus, playerStatus, syncStatus } from '../../lib/status';
import { LiveControls } from './LiveControls';
import { NowPlaying } from './NowPlaying';
import { StoreAlerts } from './StoreAlerts';

/** Cartão de uma loja no Início (seção 7.2, Dashboard). */
export function StoreCard({ store }: { store: StoreSummary }) {
  const now = useNow(5000);
  const status = playerStatus(store);
  const bluetooth = bluetoothStatus(store);
  const sync = syncStatus(store);

  return (
    <Card className="flex flex-col">
      <div className="flex items-center justify-between gap-3 px-5 pt-4">
        <Link
          to={`/lojas/${store.id}`}
          className="group flex min-w-0 items-center gap-1 font-semibold text-stone-900 hover:text-brand-800"
        >
          <span className="truncate">{store.name}</span>
          <ChevronRight className="size-4 shrink-0 text-stone-400 group-hover:text-brand-700" aria-hidden />
        </Link>
        <Badge tone={status.tone} dot>
          {status.label}
        </Badge>
      </div>
      <div className="flex flex-1 flex-col gap-4 px-5 py-4">
        <StoreAlerts alerts={store.alerts} />
        <NowPlaying store={store} />
        {store.enrolled && (
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
            <div className="flex items-center gap-1.5">
              <dt className="sr-only">Caixa de som</dt>
              <Bluetooth
                className={`size-4 ${bluetooth.tone === 'green' ? 'text-emerald-600' : bluetooth.tone === 'red' ? 'text-red-600' : 'text-stone-400'}`}
                aria-hidden
              />
              <dd className="truncate">{bluetooth.label}</dd>
            </div>
            <div className="flex items-center gap-1.5">
              <dt className="sr-only">Sincronização</dt>
              <RefreshCw
                className={`size-4 ${sync.tone === 'green' ? 'text-emerald-600' : 'text-sky-600'}`}
                aria-hidden
              />
              <dd className="truncate">{sync.label}</dd>
            </div>
            <div className="flex items-center gap-1.5">
              <dt className="sr-only">Volume</dt>
              <Volume2 className="size-4 text-stone-400" aria-hidden />
              <dd>Volume {store.volume ?? '–'}</dd>
            </div>
            <div className="text-stone-500">
              <dt className="sr-only">Último sinal</dt>
              <dd>{store.online ? 'Conectada' : `Último sinal ${formatAgo(store.lastSeenAt, now)}`}</dd>
            </div>
          </dl>
        )}
      </div>
      {store.enrolled && (
        <div className="border-t border-stone-100 px-5 py-3">
          <LiveControls store={store} />
        </div>
      )}
    </Card>
  );
}
