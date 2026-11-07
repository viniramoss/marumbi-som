import type { OfferSummary, PlaylistUsage } from '../../api/types';
import { anchorIssue, anchorIssues } from './anchorIssue';

const offer = (changes: Partial<OfferSummary> = {}) =>
  ({
    id: 1,
    name: 'Picanha',
    enabled: true,
    status: 'live',
    storeIds: [],
    validFrom: '2026-10-28T12:00:00Z',
    validUntil: '2026-11-04T12:00:00Z',
    ...changes,
  }) as OfferSummary;

const usage = (storeId: number, storeName: string) =>
  ({ storeId, storeName, days: [1, 2, 3, 4, 5, 6], start: '08:00', end: '21:00' }) as PlaylistUsage;

const loja1 = usage(1, 'Loja 1');
const loja3 = usage(3, 'Loja 3');

describe('anchorIssue', () => {
  it('oferta no ar e marcada para uma das lojas da playlist não tem problema', () => {
    expect(anchorIssue(offer(), [loja1, loja3])).toBeNull();
    // Oferta só da Loja 3 numa playlist que a Loja 1 também toca: é o normal, não é aviso.
    expect(anchorIssue(offer({ storeIds: [3] }), [loja1, loja3])).toBeNull();
  });

  it('vencida diz quando venceu e o que fazer', () => {
    const issue = anchorIssue(offer({ status: 'expired' }), [loja3]);
    expect(issue?.label).toBe('Vencida');
    expect(issue?.detail).toMatch(/^Venceu em .*04\/11\/2026.* e não toca mais\. Abra a oferta e mude o Termina/);
  });

  it('desativada vem antes de vencida', () => {
    expect(anchorIssue(offer({ enabled: false, status: 'disabled' }), [loja3])?.label).toBe('Desativada');
    expect(anchorIssue(offer({ enabled: false, status: 'expired' }), [loja3])?.label).toBe('Desativada');
  });

  it('marcada só para lojas que não tocam a playlist', () => {
    const issue = anchorIssue(offer({ storeIds: [2] }), [loja1, loja3]);
    expect(issue?.label).toBe('Fora das lojas');
    expect(issue?.detail).toContain('Loja 1, Loja 3');
    // Playlist fora da agenda: não dá para saber as lojas, então não avisa.
    expect(anchorIssue(offer({ storeIds: [2] }), [])).toBeNull();
  });

  it('agendada diz quando começa', () => {
    expect(anchorIssue(offer({ status: 'scheduled' }), [loja3])?.detail).toMatch(/^Começa a tocar em .*28\/10\/2026/);
  });

  it('oferta que não está mais no cadastro não gera aviso', () => {
    expect(anchorIssue(undefined, [loja3])).toBeNull();
  });
});

describe('anchorIssues', () => {
  it('junta por oferta e ignora as que estão no ar', () => {
    const offers = [offer({ id: 1, status: 'expired' }), offer({ id: 2 })];
    const issues = anchorIssues([1, 1, 2], offers, [loja3]);
    expect([...issues.keys()]).toEqual([1]);
    expect(issues.get(1)?.label).toBe('Vencida');
  });
});
