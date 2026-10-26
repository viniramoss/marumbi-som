import { act, screen } from '@testing-library/react';
import { keys } from '../api/queries';
import type { HubUpdateStatus } from '../api/types';
import { renderScreen } from '../test/render';
import { HubUpdatedBanner } from './HubUpdatedBanner';

const status = (currentVersion: string): HubUpdateStatus => ({
  currentVersion,
  supported: true,
  updaterInstalled: true,
  available: null,
  download: null,
  message: null,
  problem: false,
  autoUpdate: true,
  autoUpdateTime: '03:00',
  pending: null,
  last: null,
});

describe('HubUpdatedBanner', () => {
  it('pede para recarregar quando o Hub volta numa versão diferente da que abriu a página', async () => {
    const { client } = renderScreen(<HubUpdatedBanner />, [[keys.hubUpdate, status('0.6.41')]]);
    expect(screen.queryByRole('button', { name: 'Recarregar' })).not.toBeInTheDocument();

    act(() => client.setQueryData(keys.hubUpdate, status('0.6.42')));

    // O TanStack Query avisa os componentes no próximo ciclo (setTimeout), daí o findBy.
    expect(await screen.findByText(/O Hub foi atualizado para a versão/)).toHaveTextContent('0.6.42');
    expect(screen.getByRole('button', { name: 'Recarregar' })).toBeInTheDocument();
  });
});
