import { screen } from '@testing-library/react';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { StoreCard } from './StoreCard';

describe('StoreCard', () => {
  it('loja vinculada sem agenda avisa que não toca e leva para a agenda', () => {
    renderScreen(
      <StoreCard store={storeSummary({ hasSchedule: false, open: false, player: 'idle', nowPlaying: null })} />,
    );

    expect(screen.getByText('Sem agenda', { exact: true })).toBeInTheDocument();
    expect(screen.getByText(/a loja não toca nada/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Montar a agenda' })).toHaveAttribute('href', '/lojas/1?aba=agenda');
  });

  it('com agenda não mostra o aviso', () => {
    renderScreen(<StoreCard store={storeSummary()} />);

    expect(screen.queryByRole('link', { name: 'Montar a agenda' })).not.toBeInTheDocument();
  });
});
