import { playlistDetail } from '../../test/fixtures';
import {
  addTracks,
  anchorsChanged,
  draftFrom,
  isDirty,
  moveItem,
  putAnchor,
  removeAnchor,
  removeItem,
  setShuffle,
  settingsChanged,
  startTimes,
  toAnchorsInput,
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

  describe('ofertas presas (âncoras)', () => {
    const anchored = playlistDetail({
      anchors: [
        {
          id: 7,
          offerId: 3,
          offerName: 'Picanha',
          playlistId: 1,
          playlistName: 'Manhã',
          itemId: 'item-2',
          position: 2,
          trackTitle: 'Fio de Cabelo',
          kind: 'anchorDuring',
          offsetMs: 80_000,
        },
      ],
    });

    it('reordenar não mexe na âncora; ajustar o tempo conta como mudança', () => {
      const draft = moveItem(draftFrom(anchored), 'item-2', 'item-1');
      expect(anchorsChanged(draft, anchored)).toBe(false);
      const moved = putAnchor(draft, { ...draft.anchors[0]!, offsetMs: 90_000 });
      expect(anchorsChanged(moved, anchored)).toBe(true);
      expect(isDirty(removeAnchor(draftFrom(anchored), 'ancora-7'), anchored)).toBe(true);
    });

    it('o tempo fica entre 0 e 5 s antes do fim da música', () => {
      const draft = putAnchor(draftFrom(saved), {
        offerId: 3,
        offerName: 'Picanha',
        itemKey: 'item-1',
        offsetMs: 999_000,
      });
      expect(draft.anchors[0]).toMatchObject({ itemKey: 'item-1', offsetMs: 235_000, kind: 'anchorDuring' });
      expect(putAnchor(draft, { ...draft.anchors[0]!, offsetMs: -10 }).anchors[0]!.offsetMs).toBe(0);
    });

    it('tirar a música solta a oferta presa a ela', () => {
      expect(removeItem(draftFrom(anchored), 'item-2').anchors).toEqual([]);
      expect(removeItem(draftFrom(anchored), 'item-1').anchors).toHaveLength(1);
    });

    it('ligar o aleatório solta todas as ofertas presas', () => {
      expect(setShuffle(draftFrom(anchored), true).anchors).toEqual([]);
    });

    it('âncora em música nova usa o id que o Hub deu ao salvar os itens', () => {
      let draft = addTracks(draftFrom(saved), [{ hash: 'h3', title: 'Nova', artist: null, durationMs: 100_000 }]);
      const newKey = draft.items[2]!.key;
      draft = putAnchor(draft, { offerId: 3, offerName: 'Picanha', itemKey: newKey, offsetMs: 10_000 });
      expect(anchorsChanged(draft, saved)).toBe(true);
      const savedItems = [
        ...saved.items,
        { id: 'item-3', hash: 'h3', title: 'Nova', artist: null, durationMs: 100_000 },
      ];
      expect(toAnchorsInput(draft, savedItems)).toEqual([
        { offerId: 3, itemId: 'item-3', offsetMs: 10_000, kind: 'anchorDuring' },
      ]);
    });
  });
});
