import { Megaphone } from 'lucide-react';
import type { StoreSummary } from '../../api/types';
import { useNow } from '../../hooks/useNow';
import { formatAgo, formatIn, formatTime } from '../../lib/format';

const results: Record<string, string> = { interrupted: 'cortada', played: 'tocou' };

/** Próxima oferta prevista pelo agente e última tocada (seção 7.2, D26 d). */
export function OfferInfo({ store, showEmpty = false }: { store: StoreSummary; showEmpty?: boolean }) {
  const now = useNow(15_000);
  const { nextOffer, lastOffer } = store;
  if (!nextOffer && !lastOffer && !showEmpty) return null;

  return (
    <div className="flex gap-2 text-sm">
      <Megaphone className="mt-0.5 size-4 shrink-0 text-amber-600" aria-hidden />
      <dl className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap gap-x-1.5">
          <dt className="text-stone-500">Próxima oferta:</dt>
          {nextOffer ? (
            <dd>
              <span className="font-medium text-stone-900">{nextOffer.name}</span>{' '}
              <span className="text-stone-500 tabular-nums">
                às {formatTime(nextOffer.at)} ({formatIn(nextOffer.at, now)})
              </span>
            </dd>
          ) : (
            <dd className="text-stone-500">nenhuma prevista</dd>
          )}
        </div>
        <div className="flex flex-wrap gap-x-1.5">
          <dt className="text-stone-500">Última:</dt>
          {lastOffer ? (
            <dd>
              <span className="text-stone-800">{lastOffer.name}</span>{' '}
              <span className="text-stone-500 tabular-nums">
                às {formatTime(lastOffer.at)} ({formatAgo(lastOffer.at, now)})
                {lastOffer.result &&
                  lastOffer.result !== 'played' &&
                  `, ${results[lastOffer.result] ?? lastOffer.result}`}
              </span>
            </dd>
          ) : (
            <dd className="text-stone-500">nenhuma nos últimos 7 dias</dd>
          )}
        </div>
      </dl>
    </div>
  );
}
