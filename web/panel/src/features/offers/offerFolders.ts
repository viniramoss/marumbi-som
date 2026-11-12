import type { OfferSummary, StoreSummary } from '../../api/types';

/**
 * Pastas de ofertas (D40): a pasta de uma oferta é a loja onde ela toca. "Geral" = todas as lojas (nenhuma marcada,
 * ou todas marcadas). Uma oferta marcada para a Loja 1 e a Loja 3 aparece nas duas pastas.
 */
export type Folder = 'todas' | 'geral' | number;

type Store = Pick<StoreSummary, 'id' | 'name'>;
type Offer = Pick<OfferSummary, 'storeIds'>;

/** Oferta que toca em todas as lojas. */
export function isGeneral(offer: Offer, stores: readonly Store[]): boolean {
  if (offer.storeIds.length === 0) return true;
  return stores.length > 1 && stores.every((s) => offer.storeIds.includes(s.id));
}

export function inFolder(offer: Offer, folder: Folder, stores: readonly Store[]): boolean {
  if (folder === 'todas') return true;
  if (folder === 'geral') return isGeneral(offer, stores);
  return !isGeneral(offer, stores) && offer.storeIds.includes(folder);
}

/** "?pasta=geral" ou "?pasta=3" → pasta; o que não existir mais volta para "todas". */
export function parseFolder(value: string | null, stores: readonly Store[]): Folder {
  if (value === 'geral') return 'geral';
  const id = Number(value);
  return Number.isInteger(id) && stores.some((s) => s.id === id) ? id : 'todas';
}

/** Lojas marcadas numa oferta nova criada de dentro da pasta (Geral ou Todas = todas as lojas). */
export function storeIdsForFolder(folder: Folder, stores: readonly Store[]): number[] {
  return typeof folder === 'number' ? [folder] : stores.map((s) => s.id);
}

/**
 * Ofertas que aparecem no editor de uma playlist (D40): a playlist geral (sem loja) mostra todas; a exclusiva de uma
 * ou mais lojas mostra as da pasta Geral e as dessas lojas.
 */
export function offersForPlaylist<T extends Offer>(
  offers: readonly T[],
  playlistStoreIds: readonly number[],
  stores: readonly Store[],
): T[] {
  if (playlistStoreIds.length === 0) return [...offers];
  return offers.filter((o) => isGeneral(o, stores) || o.storeIds.some((id) => playlistStoreIds.includes(id)));
}

/** "Loja 1", "Loja 1 e Loja 3". */
export function storeNames(ids: readonly number[], stores: readonly Store[]): string {
  const names = stores.filter((s) => ids.includes(s.id)).map((s) => s.name);
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} e ${names[names.length - 1]!}`;
}
