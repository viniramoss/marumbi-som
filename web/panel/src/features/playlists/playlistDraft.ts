import type { AnchorInput, ItemInput, PlaylistDetail } from '../../api/types';

/** Item da playlist sendo editada. `id` é o UUID estável do Hub (nulo em item novo); `key` só identifica na tela. */
export interface DraftItem {
  key: string;
  id: string | null;
  hash: string;
  title: string;
  artist: string | null;
  durationMs: number;
}

/** Oferta presa a uma música (âncora "durante", D24 e D26 c). Aponta para o item pela `key` da tela. */
export interface DraftAnchor {
  key: string;
  offerId: number;
  offerName: string;
  itemKey: string;
  kind: string;
  offsetMs: number | null;
}

export interface Draft {
  name: string;
  shuffle: boolean;
  items: DraftItem[];
  anchors: DraftAnchor[];
}

/** A oferta presa termina pelo menos 5 s antes do fim da música (mesma margem do Hub e do agente). */
export const AnchorEndMarginMs = 5000;

export const maxAnchorOffset = (durationMs: number) => Math.max(0, durationMs - AnchorEndMarginMs);

/** "1:20" → 80 000 ms; "80" também vale (segundos). Nulo se não der para ler. */
export function parseClock(text: string): number | null {
  const match = /^\s*(?:(\d+):)?(\d{1,3})\s*$/.exec(text);
  if (!match) return null;
  const minutes = match[1] ? Number(match[1]) : 0;
  const seconds = Number(match[2]);
  if (match[1] && seconds >= 60) return null;
  return (minutes * 60 + seconds) * 1000;
}

let counter = 0;
export const newKey = () => `novo-${++counter}`;

export function draftFrom(playlist: PlaylistDetail): Draft {
  return {
    name: playlist.name,
    shuffle: playlist.shuffle,
    items: playlist.items.map((item) => ({ ...item, key: item.id })),
    anchors: playlist.anchors.map((anchor) => ({
      key: `ancora-${anchor.id}`,
      offerId: anchor.offerId,
      offerName: anchor.offerName,
      itemKey: anchor.itemId,
      kind: anchor.kind,
      offsetMs: anchor.offsetMs,
    })),
  };
}

export function addTracks(
  draft: Draft,
  tracks: { hash: string; title: string; artist: string | null; durationMs: number }[],
): Draft {
  return { ...draft, items: [...draft.items, ...tracks.map((t) => ({ ...t, id: null, key: newKey() }))] };
}

/** Tira a música e solta as ofertas presas a ela (o editor avisa antes, seção 7.2). */
export function removeItem(draft: Draft, key: string): Draft {
  return {
    ...draft,
    items: draft.items.filter((item) => item.key !== key),
    anchors: draft.anchors.filter((anchor) => anchor.itemKey !== key),
  };
}

export const anchorsOf = (draft: Draft, itemKey: string) => draft.anchors.filter((a) => a.itemKey === itemKey);

/** Prende uma oferta a uma música ou ajusta uma já presa (`key` existente). */
export function putAnchor(
  draft: Draft,
  anchor: { key?: string; offerId: number; offerName: string; itemKey: string; offsetMs: number },
): Draft {
  const item = draft.items.find((i) => i.key === anchor.itemKey);
  if (!item) return draft;
  const offsetMs = Math.min(Math.max(0, Math.round(anchor.offsetMs)), maxAnchorOffset(item.durationMs));
  const next: DraftAnchor = { ...anchor, key: anchor.key ?? newKey(), kind: 'anchorDuring', offsetMs };
  const exists = draft.anchors.some((a) => a.key === next.key);
  return {
    ...draft,
    anchors: exists ? draft.anchors.map((a) => (a.key === next.key ? next : a)) : [...draft.anchors, next],
  };
}

export function removeAnchor(draft: Draft, key: string): Draft {
  return { ...draft, anchors: draft.anchors.filter((a) => a.key !== key) };
}

/** Ligar o aleatório solta todas as ofertas presas (âncoras só existem em playlist sequencial). */
export function setShuffle(draft: Draft, shuffle: boolean): Draft {
  return { ...draft, shuffle, anchors: shuffle ? [] : draft.anchors };
}

export function moveItem(draft: Draft, fromKey: string, toKey: string): Draft {
  const from = draft.items.findIndex((i) => i.key === fromKey);
  const to = draft.items.findIndex((i) => i.key === toKey);
  if (from < 0 || to < 0 || from === to) return draft;
  const items = [...draft.items];
  const [moved] = items.splice(from, 1);
  items.splice(to, 0, moved!);
  return { ...draft, items };
}

/** Corpo do PUT /items: itens existentes mantêm o id (âncoras de ofertas apontam para ele), novos vão sem id. */
export function toItemsInput(draft: Draft): ItemInput[] {
  return draft.items.map((item) => (item.id ? { id: item.id, hash: item.hash } : { hash: item.hash }));
}

/** Início de cada música a partir do começo da playlist (tempo acumulado da seção 7.2). */
export function startTimes(items: readonly DraftItem[]): number[] {
  const starts: number[] = [];
  let total = 0;
  for (const item of items) {
    starts.push(total);
    total += item.durationMs;
  }
  return starts;
}

export const totalDuration = (items: readonly DraftItem[]) => items.reduce((sum, item) => sum + item.durationMs, 0);

function itemsChanged(draft: Draft, saved: PlaylistDetail): boolean {
  if (draft.items.length !== saved.items.length) return true;
  return draft.items.some(
    (item, index) => item.id !== saved.items[index]?.id || item.hash !== saved.items[index]?.hash,
  );
}

const anchorSignature = (anchors: readonly { offerId: number; itemId: string | null; offsetMs: number | null }[]) =>
  anchors
    .map((a) => `${a.offerId}|${a.itemId ?? '?'}|${a.offsetMs ?? ''}`)
    .sort()
    .join(',');

/** As âncoras mudaram em relação ao salvo (inclusive uma presa a música ainda não salva). */
export function anchorsChanged(draft: Draft, saved: PlaylistDetail): boolean {
  const ids = new Map(draft.items.map((item) => [item.key, item.id]));
  return (
    anchorSignature(draft.anchors.map((a) => ({ ...a, itemId: ids.get(a.itemKey) ?? null }))) !==
    anchorSignature(saved.anchors.map((a) => ({ offerId: a.offerId, itemId: a.itemId, offsetMs: a.offsetMs })))
  );
}

export function isDirty(draft: Draft, saved: PlaylistDetail): boolean {
  if (draft.name.trim() !== saved.name || draft.shuffle !== saved.shuffle) return true;
  return itemsChanged(draft, saved) || anchorsChanged(draft, saved);
}

/**
 * Corpo do PUT /anchors, depois de salvar os itens: o Hub devolve os itens na ordem enviada, então a posição liga a
 * `key` da tela ao id definitivo (item novo só ganha id ao salvar).
 */
export function toAnchorsInput(draft: Draft, savedItems: PlaylistDetail['items']): AnchorInput[] {
  const ids = new Map(draft.items.map((item, index) => [item.key, savedItems[index]?.id]));
  return draft.anchors.flatMap((anchor) => {
    const itemId = ids.get(anchor.itemKey);
    return itemId ? [{ offerId: anchor.offerId, itemId, offsetMs: anchor.offsetMs, kind: anchor.kind }] : [];
  });
}

export const settingsChanged = (draft: Draft, saved: PlaylistDetail) =>
  draft.name.trim() !== saved.name || draft.shuffle !== saved.shuffle;
