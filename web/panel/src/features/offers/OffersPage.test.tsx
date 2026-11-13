import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { keys, useChangeOfferStores, useChangeOfferValidity } from '../../api/queries';
import type { OfferSummary } from '../../api/types';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { OffersPage } from './OffersPage';

const changeValidity = vi.fn();
const changeStores = vi.fn();

vi.mock('../../api/queries', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../api/queries')>()),
  useChangeOfferValidity: vi.fn(),
  useChangeOfferStores: vi.fn(),
}));

beforeEach(() => {
  changeValidity.mockReset();
  changeValidity.mockResolvedValue([]);
  vi.mocked(useChangeOfferValidity).mockReturnValue({ mutateAsync: changeValidity } as never);
  changeStores.mockReset();
  changeStores.mockResolvedValue([]);
  vi.mocked(useChangeOfferStores).mockReturnValue({ mutateAsync: changeStores } as never);
});

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

  it('marca as vencidas e prorroga 15 dias de uma vez (D39)', async () => {
    const user = userEvent.setup();
    const { router } = renderScreen(<OffersPage />, [
      [
        keys.offers,
        [
          offer({ id: 3, name: 'Horário', status: 'expired' }),
          offer({ id: 4, name: 'QR Code', status: 'expired' }),
          offer({ id: 5, name: 'Picanha' }),
        ],
      ],
      [keys.stores, []],
    ]);

    await user.click(screen.getByRole('button', { name: 'Marcar as 2 vencidas' }));
    expect(screen.getByText('2 selecionadas')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Selecionar Horário' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Selecionar Picanha' })).not.toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Prorrogar 15 dias' }));

    expect(changeValidity).toHaveBeenCalledWith({ offerIds: [3, 4], extendDays: 15, validUntil: null });
    expect(await screen.findByText('2 ofertas prorrogadas por 15 dias.')).toBeInTheDocument();
    expect(screen.queryByText('2 selecionadas')).not.toBeInTheDocument();
    // Marcar não abre a oferta.
    expect(router.state.location.pathname).toBe('/');
  });

  it('marca todas e põe a mesma data de fim, recusando data antes do início', async () => {
    const user = userEvent.setup();
    renderScreen(<OffersPage />, [
      [
        keys.offers,
        [offer({ id: 3, name: 'Horário' }), offer({ id: 4, name: 'Natal', validFrom: '2026-12-20T03:00:00Z' })],
      ],
      [keys.stores, []],
    ]);

    await user.click(screen.getByRole('checkbox', { name: 'Selecionar todas as ofertas' }));
    expect(screen.getByText('2 selecionadas')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Escolher a data' }));
    const dialog = screen.getByRole('dialog', { name: 'Escolher o fim da validade' });
    const date = within(dialog).getByLabelText('Termina em');

    await user.clear(date);
    await user.type(date, '2026-12-10T23:59');
    expect(within(dialog).getByText(/antes do início de: Natal/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Aplicar nas 2 ofertas' })).toBeDisabled();

    await user.clear(date);
    await user.type(date, '2026-12-31T23:59');
    await user.click(within(dialog).getByRole('button', { name: 'Aplicar nas 2 ofertas' }));

    expect(changeValidity).toHaveBeenCalledWith({
      offerIds: [3, 4],
      extendDays: null,
      validUntil: new Date(2026, 11, 31, 23, 59).toISOString(),
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('pastas por loja filtram a lista e mudam várias ofertas de pasta (D40)', async () => {
    const user = userEvent.setup();
    const stores = [storeSummary({ id: 1, name: 'Loja 1' }), storeSummary({ id: 3, name: 'Loja 3' })];
    renderScreen(<OffersPage />, [
      [
        keys.offers,
        [
          offer({ id: 1, name: 'Horário de atendimento', storeIds: [] }),
          offer({ id: 2, name: 'Picanha', storeIds: [1] }),
          offer({ id: 3, name: 'Costela', storeIds: [3] }),
        ],
      ],
      [keys.stores, stores],
    ]);

    const folders = screen.getByRole('navigation', { name: 'Pastas de ofertas' });
    await user.click(within(folders).getByRole('button', { name: /Loja 3/ }));
    expect(screen.getByText('Costela')).toBeInTheDocument();
    expect(screen.queryByText('Picanha')).not.toBeInTheDocument();
    expect(screen.queryByText('Horário de atendimento')).not.toBeInTheDocument();

    await user.click(within(folders).getByRole('button', { name: /Geral/ }));
    expect(screen.getByText('Horário de atendimento')).toBeInTheDocument();
    expect(screen.queryByText('Costela')).not.toBeInTheDocument();

    await user.click(within(folders).getByRole('button', { name: /Todas/ }));
    await user.click(screen.getByLabelText('Selecionar Picanha'));
    await user.click(screen.getByLabelText('Selecionar Costela'));
    await user.click(screen.getByRole('button', { name: 'Mudar de pasta' }));
    const dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Loja 1' }));
    await user.click(within(dialog).getByRole('button', { name: 'Mover para Loja 1' }));
    expect(changeStores).toHaveBeenCalledWith({ offerIds: [2, 3], storeIds: [1] });
  });
});
