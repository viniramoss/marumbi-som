import { useMemo, useState } from 'react';
import { History, Megaphone } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useHistory } from '../../api/queries';
import { Badge, Card, CardHeader, EmptyState, ErrorNote, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { formatTime, formatWeekdayDate } from '../../lib/format';
import { dayCounts, groupByDay, periodStart, type Period } from './history';

/** Aba "Histórico": o que tocou hoje ou nos últimos 7 dias (seção 7.2). */
export function HistoryTab({ storeId }: { storeId: number }) {
  const [period, setPeriod] = useState<Period>('hoje');
  const today = new Date(useNow(60_000)).toDateString();
  const since = useMemo(() => periodStart(period, new Date(today)), [period, today]);
  const history = useHistory(storeId, since);
  const groups = useMemo(() => groupByDay(history.data ?? []), [history.data]);

  return (
    <Card>
      <CardHeader
        title="O que tocou"
        subtitle="Músicas e ofertas iniciadas pela loja. Ofertas descartadas (que não chegaram a tocar) não aparecem."
        actions={
          <div className="inline-flex rounded-lg bg-stone-100 p-0.5" role="group" aria-label="Período">
            {(
              [
                ['hoje', 'Hoje'],
                ['semana', '7 dias'],
              ] as const
            ).map(([id, label]) => (
              <button
                key={id}
                type="button"
                aria-pressed={period === id}
                onClick={() => setPeriod(id)}
                className={`rounded-md px-3 py-1 text-sm font-medium ${period === id ? 'bg-white shadow-sm' : 'text-stone-600 hover:text-stone-900'}`}
              >
                {label}
              </button>
            ))}
          </div>
        }
      />
      {history.isPending ? (
        <Spinner />
      ) : history.isError ? (
        <div className="p-5">
          <ErrorNote>{errorMessage(history.error)}</ErrorNote>
        </div>
      ) : history.data.length === 0 ? (
        <EmptyState
          icon={<History className="size-8" />}
          title={period === 'hoje' ? 'Nada tocou hoje ainda' : 'Nada tocou nos últimos 7 dias'}
        />
      ) : (
        <div className="divide-y divide-stone-100">
          {groups.map((group) => (
            <section key={group.day}>
              <h3 className="sticky top-0 bg-stone-50 px-5 py-1.5 text-xs font-semibold text-stone-500">
                <span className="capitalize">{formatWeekdayDate(group.entries[0]!.startedAt)}</span> ·{' '}
                {dayCounts(group.entries)}
              </h3>
              <ol className="divide-y divide-stone-100">
                {group.entries.map((entry, index) => (
                  <li
                    key={`${entry.startedAt}-${index}`}
                    className={`flex items-center gap-4 px-5 py-2 text-sm ${entry.kind === 'offer' ? 'bg-amber-50/50' : ''}`}
                  >
                    <span className="w-12 shrink-0 text-stone-500 tabular-nums">{formatTime(entry.startedAt)}</span>
                    {entry.kind === 'offer' ? (
                      <span className="flex min-w-0 flex-1 items-center gap-2">
                        <Megaphone className="size-4 shrink-0 text-amber-600" aria-label="Oferta" />
                        <span className="truncate font-medium text-stone-900">{entry.title ?? 'Oferta removida'}</span>
                        {entry.result === 'interrupted' && <Badge tone="amber">cortada</Badge>}
                      </span>
                    ) : (
                      <span className="min-w-0 flex-1 truncate">
                        <span className="font-medium text-stone-900">{entry.title ?? 'Música fora da biblioteca'}</span>
                        {entry.artist && <span className="text-stone-500"> · {entry.artist}</span>}
                      </span>
                    )}
                  </li>
                ))}
              </ol>
            </section>
          ))}
          {history.data.length >= 2000 && (
            <p className="px-5 py-3 text-xs text-stone-500">Mostrando os 2.000 registros mais recentes do período.</p>
          )}
        </div>
      )}
    </Card>
  );
}
