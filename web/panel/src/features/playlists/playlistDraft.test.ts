import { playlistDetail } from '../../test/fixtures';
import {
  addTracks,
  draftFrom,
  isDirty,
  moveItem,
  removeItem,
  settingsChanged,
  startTimes,
  toItemsInput,
} from './playlistDraft';

describe('playlistDraft', () => {
  const saved = playlistDetail();

  it('começa igual ao salvo', () => {
    expect(isDirty(draftFrom(saved), saved)).toBe(false);
  });

  it('reordenar mantém o id de cada item (as âncoras de ofertas apontam para ele)', () => {
    const moved = moveItem(draftFrom(saved), 'item-2', 'item-1');
    expect(toItemsInput(moved)).toEqual([
      { id: 'item-2', hash: 'h2' },
      { id: 'item-1', hash: 'h1' },
    ]);
    expect(isDirty(moved, saved)).toBe(true);
  });

  it('item novo vai sem id, e a mesma música pode entrar duas vezes', () => {
    const draft = addTracks(draftFrom(saved), [{ hash: 'h1', title: 'Evidências', artist: null, durationMs: 240_000 }]);
    expect(toItemsInput(draft).at(-1)).toEqual({ hash: 'h1' });
    expect(draft.items).toHaveLength(3);
  });

  it('remover e o tempo acumulado de cada música', () => {
    const draft = draftFrom(saved);
    expect(startTimes(draft.items)).toEqual([0, 240_000]);
    expect(removeItem(draft, 'item-1').items.map((i) => i.id)).toEqual(['item-2']);
  });

  it('nome e aleatório só contam como mudança de configuração', () => {
    const draft = { ...draftFrom(saved), name: '  Manhã  ' };
    expect(settingsChanged(draft, saved)).toBe(false);
    expect(settingsChanged({ ...draft, shuffle: true }, saved)).toBe(true);
  });
});
