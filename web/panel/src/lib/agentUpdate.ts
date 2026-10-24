import type { ReleaseInfo, StoreSummary, UpdateInfo } from '../api/types';

/** Compara versões de três números (0.6.9 < 0.6.10), como o Hub e o agente; fora do padrão conta como menor. */
export function compareVersions(a: string | null | undefined, b: string | null | undefined): number {
  const parts = (version: string | null | undefined) =>
    version && /^\d+\.\d+\.\d+$/.test(version) ? version.split('.').map(Number) : [-1, -1, -1];
  const [x, y] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) {
    if (x[i] !== y[i]) return x[i]! < y[i]! ? -1 : 1;
  }
  return 0;
}

export function isUpdating(update: UpdateInfo | null | undefined): boolean {
  return update?.stage === 'downloading' || update?.stage === 'verifying' || update?.stage === 'installing';
}

/** Lojas conectadas numa versão anterior à de distribuição e sem atualização em andamento. */
export function storesToUpdate(stores: readonly StoreSummary[], current: ReleaseInfo | undefined): StoreSummary[] {
  if (!current) return [];
  return stores.filter(
    (s) => s.online && s.enrolled && !isUpdating(s.update) && compareVersions(s.agentVersion, current.version) < 0,
  );
}

/** Por que o botão "Atualizar" de uma loja está desligado; nulo se pode atualizar. */
export function updateBlocker(store: StoreSummary, current: ReleaseInfo | undefined): string | null {
  if (!current) return 'Nenhuma versão para distribuição. Envie o pacote em Configurações.';
  if (!store.online) return 'A loja está desconectada do Hub.';
  if (isUpdating(store.update)) return 'Já há uma atualização em andamento.';
  if (compareVersions(store.agentVersion, current.version) >= 0)
    return `A loja já está na versão ${store.agentVersion}.`;
  return null;
}
