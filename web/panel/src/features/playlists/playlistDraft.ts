import type { ItemInput, PlaylistDetail } from '../../api/types';

/** Item da playlist sendo editada. `id` é o UUID estável do Hub (nulo em item novo); `key` só identifica na tela. */
export interface DraftItem {
  key: string;
  id: string | null;
  hash: string;
  title: string;
  artist: string | null;
  durationMs: number;
}

export interface Draft {
  name: string;
  shuffle: boolean;
  items: DraftItem[];
}

let counter = 0;
export const newKey = () => `novo-${++counter}`;

export function draftFrom(playlist: PlaylistDetail): Draft {
  return {
    name: playlist.name,
    shuffle: playlist.shuffle,
    items: playlist.items.map((item) => ({ ...item, key: item.id })),
  };
}

export function addTracks(
  draft: Draft,
  tracks: { hash: string; title: string; artist: string | null; durationMs: number }[],
): Draft {
  return { ...draft, items: [...draft.items, ...tracks.map((t) => ({ ...t, id: null, key: newKey() }))] };
}

export function removeItem(draft: Draft, key: string): Draft {
  return { ...draft, items: draft.items.filter((item) => item.key !== key) };
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

export function isDirty(draft: Draft, saved: PlaylistDetail): boolean {
  if (draft.name.trim() !== saved.name || draft.shuffle !== saved.shuffle) return true;
  if (draft.items.length !== saved.items.length) return true;
  return draft.items.some(
    (item, index) => item.id !== saved.items[index]?.id || item.hash !== saved.items[index]?.hash,
  );
}

export const settingsChanged = (draft: Draft, saved: PlaylistDetail) =>
  draft.name.trim() !== saved.name || draft.shuffle !== saved.shuffle;
