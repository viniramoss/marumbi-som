import type { StoreSummary } from '../api/types';
import { ProgressBar } from './ui';

/** Barra do download de uma versão nova no PC da loja ("Baixando 3/10"); não aparece fora do download. */
export function SyncProgress({ store, className = '' }: { store: StoreSummary; className?: string }) {
  if (store.pendingVersion === null || store.syncTotal === null || store.syncTotal === 0) return null;
  return (
    <div className={className}>
      <ProgressBar
        value={store.syncDone ?? 0}
        max={store.syncTotal}
        label={`Baixando a versão ${store.pendingVersion} na loja ${store.name}`}
      />
    </div>
  );
}
