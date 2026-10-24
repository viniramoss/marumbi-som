import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { StoreCard } from './StoreCard';

const noSchedule = storeSummary({ hasSchedule: false, open: false, player: 'idle', nowPlaying: null });

describe('StoreCard', () => {
  it('loja vinculada sem agenda avisa que não toca e leva para a agenda', () => {
    renderScreen(<StoreCard store={noSchedule} />);

    expect(screen.getByText('Sem agenda', { exact: true })).toBeInTheDocument();
    expect(screen.getByText(/a loja não toca nada/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Montar a agenda' })).toHaveAttribute('href', '/lojas/1?aba=agenda');
  });

  it('com agenda não mostra o aviso', () => {
    renderScreen(<StoreCard store={storeSummary()} />);

    expect(screen.queryByRole('link', { name: 'Montar a agenda' })).not.toBeInTheDocument();
  });

  it('clicar em qualquer parte do card abre a loja', async () => {
    const user = userEvent.setup();
    const { router } = renderScreen(<StoreCard store={storeSummary()} />);

    await user.click(screen.getByText('Volume 70'));

    expect(router.state.location.pathname).toBe('/lojas/1');
  });

  it('links dentro do card seguem com o destino deles', async () => {
    const user = userEvent.setup();
    const { router } = renderScreen(<StoreCard store={noSchedule} />);

    await user.click(screen.getByRole('link', { name: 'Montar a agenda' }));

    expect(router.state.location.pathname).toBe('/lojas/1');
    expect(router.state.location.search).toBe('?aba=agenda');
  });

  it('mostra a barra do download de uma versão nova', () => {
    renderScreen(<StoreCard store={storeSummary({ pendingVersion: 4, syncDone: 3, syncTotal: 10 })} />);

    expect(screen.getByText('Baixando 3/10')).toBeInTheDocument();
    const bar = screen.getByRole('progressbar', { name: /Baixando a versão 4/ });
    expect(bar).toHaveAttribute('aria-valuenow', '3');
    expect(bar).toHaveAttribute('aria-valuemax', '10');
  });
});
