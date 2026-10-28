import type { OfferDetail } from '../../api/types';
import {
  gapWarning,
  draftFromOffer,
  draftProblem,
  isOfferDirty,
  newOfferDraft,
  newRule,
  ruleText,
  toOfferInput,
} from './offerDraft';

const detail: OfferDetail = {
  offer: {
    id: 3,
    key: 'of-3',
    name: 'Picanha R$ 69,90',
    audioHash: 'a1',
    durationMs: 12_000,
    validFrom: '2026-10-23T13:00:00Z',
    validUntil: '2026-10-30T02:59:00Z',
    duckLevel: 30,
    enabled: true,
    storeIds: [2, 1],
    status: 'live',
    expiresSoon: false,
    ruleCount: 2,
    anchorCount: 0,
    plays7d: 4,
    updatedAt: '2026-10-23T13:00:00Z',
  },
  rules: [
    { kind: 'everyMinutes', id: 10, mode: 'during', minutes: 15, time: null, days: null, n: null, playlistIds: null },
    { kind: 'everyTracks', id: 11, mode: 'between', minutes: null, time: null, days: null, n: 5, playlistIds: null },
  ],
  anchors: [],
  plays: [],
};

describe('offerDraft', () => {
  it('oferta nova vale de agora até o fim do sétimo dia, em todas as lojas, a cada 30 min', () => {
    const draft = newOfferDraft(new Date(2026, 9, 24, 10, 15), [1, 2]);
    expect(draft.validFrom).toBe('2026-10-24T10:15');
    expect(draft.validUntil).toBe('2026-10-31T23:59');
    expect(draft.storeIds).toEqual([1, 2]);
    expect(draft.rules).toMatchObject([{ kind: 'everyMinutes', minutes: 30 }]);
    expect(draft.duckLevel).toBe(30);
  });

  it('o painel só cria regras durante a música (D24)', () => {
    const draft = { ...newOfferDraft(new Date(2026, 9, 24, 10, 0), []), audioHash: 'a1', name: ' Picanha ' };
    const clock = { ...newRule('clock'), time: '12:30', days: [5, 1, 3], playlistIds: [4] };
    const input = toOfferInput({ ...draft, rules: [draft.rules[0]!, clock] });
    expect(input.name).toBe('Picanha');
    expect(input.rules).toEqual([
      {
        kind: 'everyMinutes',
        id: null,
        mode: 'during',
        minutes: 30,
        time: null,
        days: null,
        n: null,
        playlistIds: null,
      },
      {
        kind: 'clock',
        id: null,
        mode: 'during',
        minutes: null,
        time: '12:30',
        days: [1, 3, 5],
        n: null,
        playlistIds: [4],
      },
    ]);
    expect(input.validFrom).toBe(new Date(2026, 9, 24, 10, 0).toISOString());
  });

  it('regra criada fora do painel volta igual, até ser removida', () => {
    const draft = draftFromOffer(detail);
    expect(draft.storeIds).toEqual([1, 2]);
    expect(draft.rules[1]!.original).toEqual(detail.rules[1]);
    expect(toOfferInput(draft).rules![1]).toEqual(detail.rules[1]);
    expect(isOfferDirty(draft, draftFromOffer(detail))).toBe(false);
    expect(isOfferDirty({ ...draft, rules: [draft.rules[0]!] }, draftFromOffer(detail))).toBe(true);
    expect(isOfferDirty({ ...draft, duckLevel: 40 }, draftFromOffer(detail))).toBe(true);
  });

  it('confere o básico antes de enviar', () => {
    const draft = draftFromOffer(detail);
    expect(draftProblem(draft)).toBeNull();
    expect(draftProblem({ ...draft, audioHash: null })).toMatch(/Envie o áudio/);
    expect(draftProblem({ ...draft, name: '  ' })).toBe('Dê um nome para a oferta.');
    expect(draftProblem({ ...draft, validUntil: draft.validFrom })).toMatch(/depois do início/);
    expect(draftProblem({ ...draft, rules: [{ ...newRule('clock'), days: [] }] })).toMatch(/dia da semana/);
    expect(draftProblem({ ...draft, rules: [{ ...newRule('everyMinutes'), minutes: Number.NaN }] })).toMatch(
      /1 a 1440/,
    );
  });

  it('descreve cada regra', () => {
    const name = (id: number) => (id === 4 ? 'Manhã' : undefined);
    expect(ruleText(detail.rules[0]!, name)).toBe('A cada 15 min');
    expect(ruleText(detail.rules[1]!, name)).toBe('A cada 5 músicas (entre músicas)');
    expect(
      ruleText(
        {
          kind: 'clock',
          id: 1,
          mode: 'during',
          minutes: null,
          time: '12:30',
          days: [1, 2, 3, 4, 5],
          n: null,
          playlistIds: [4],
        },
        name,
      ),
    ).toBe('Às 12:30, Seg a Sex · só em Manhã');
  });
});

describe('gapWarning', () => {
  const centro = { name: 'Marumbi Centro', minOfferGapS: 300 };

  it('avisa quando a regra é mais curta que o intervalo mínimo da loja', () => {
    expect(gapWarning(1, [centro])).toBe(
      'Na prática toca a cada 5 min: a loja toca no máximo 1 oferta a cada 5 min, contando todas as ofertas (Loja → Ajustes → Intervalo mínimo entre ofertas).',
    );
    expect(gapWarning(5, [centro])).toBeNull();
    expect(gapWarning(Number.NaN, [centro])).toBeNull();
    expect(gapWarning(1, [])).toBeNull();
  });

  it('com várias lojas diz quais seguram a oferta', () => {
    const batel = { name: 'Batel', minOfferGapS: 300 };
    const loja2 = { name: 'Loja 2', minOfferGapS: 60 };
    expect(gapWarning(2, [centro, batel, loja2])).toMatch(
      /^Na prática toca a cada 5 min em Marumbi Centro e Batel: as lojas tocam/,
    );
    expect(gapWarning(2, [centro, { name: 'Batel', minOfferGapS: 600 }])).toMatch(
      /^Na prática toca com menos frequência: Marumbi Centro toca no máximo 1 oferta a cada 5 min e Batel toca no máximo 1 oferta a cada 10 min,/,
    );
  });
});
