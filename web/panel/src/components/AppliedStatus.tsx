import { Link } from 'react-router';
import { useStores } from '../api/queries';
import { appliedStatus } from '../lib/status';
import { SyncProgress } from './SyncProgress';
import { Badge } from './ui';

/** Mostra se cada loja afetada já aplicou a última alteração (seção 7.1). */
export function AppliedStatus({
  storeIds,
  emptyText = 'Nenhuma loja toca esta playlist ainda.',
}: {
  storeIds: number[];
  emptyText?: string;
}) {
  const stores = useStores();
  const affected = (stores.data ?? []).filter((s) => storeIds.includes(s.id));
  if (affected.length === 0) return <p className="text-sm text-stone-500">{emptyText}</p>;
  return (
    <ul className="space-y-1.5">
      {affected.map((store) => {
        const status = appliedStatus(store);
        return (
          <li key={store.id} className="flex items-center justify-between gap-3 text-sm">
            <Link to={`/lojas/${store.id}`} className="truncate font-medium text-stone-800 hover:text-brand-800">
              {store.name}
            </Link>
            <span className="flex shrink-0 flex-col items-end gap-1">
              <Badge tone={status.tone} dot>
                {status.label}
              </Badge>
              <SyncProgress store={store} className="w-28" />
            </span>
          </li>
        );
      })}
    </ul>
  );
}
