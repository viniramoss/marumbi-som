import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { keys } from '../../api/queries';
import type { OfferSummary } from '../../api/types';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { OffersPage } from './OffersPage';

function offer(overrides: Partial<OfferSummary> = {}): OfferSummary {
  return {
    id: 3,
    key: 'of-3',
    name: 'Picanha R$ 69,90',
    audioHash: 'a1',
    durationMs: 12_000,
    validFrom: '2026-10-23T13:00:00Z',
    validUntil: '2099-10-30T02:59:00Z',
    duckLevel: 30,
    enabled: true,
    storeIds: [1],
    status: 'live',
    expiresSoon: false,
    ruleCount: 1,
    anchorCount: 2,
    plays7d: 4,
    updatedAt: '2026-10-23T13:00:00Z',
    ...overrides,
  };
}

describe('OffersPage', () => {
  it('mostra a situação, as lojas e quando toca', () => {
    renderScreen(<OffersPage />, [
      [keys.offers, [offer(), offer({ id: 4, name: 'Costela', status: 'disabled', ruleCount: 0, anchorCount: 0 })]],
      [keys.stores, [storeSummary()]],
    ]);

    expect(screen.getByText('No ar')).toBeInTheDocument();
    expect(screen.getByText('Desativada')).toBeInTheDocument();
    expect(screen.getAllByText('Marumbi Centro')).toHaveLength(2);
    expect(screen.getByText('1 regra · presa em 2 músicas')).toBeInTheDocument();
    expect(screen.getByText('Só pelo botão "tocar oferta agora"')).toBeInTheDocument();
  });

  it('avisa a oferta que vence nas próximas 24 horas', () => {
    const soon = new Date(Date.now() + 5 * 3600_000).toISOString();
    renderScreen(<OffersPage />, [
      [keys.offers, [offer({ expiresSoon: true, validUntil: soon })]],
      [keys.stores, []],
    ]);

    expect(screen.getByRole('alert')).toHaveTextContent('A oferta "Picanha R$ 69,90" vence nas próximas 24 horas.');
    expect(screen.getByText('vence em 5 h')).toBeInTheDocument();
  });

  it('clicar em qualquer parte da linha abre a oferta', async () => {
    const user = userEvent.setup();
    const { router } = renderScreen(<OffersPage />, [
      [keys.offers, [offer()]],
      [keys.stores, []],
    ]);

    await user.click(screen.getByText('1 regra · presa em 2 músicas'));

    expect(router.state.location.pathname).toBe('/ofertas/3');
  });
});
