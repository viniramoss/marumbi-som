import { screen } from '@testing-library/react';
import { keys } from '../../api/queries';
import type { ReleaseInfo } from '../../api/types';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { ReleasesCard } from './ReleasesCard';

const release = (version: string, isCurrent: boolean, storeCount: number): ReleaseInfo => ({
  version,
  sha256: 'a'.repeat(64),
  sizeBytes: 109 * 1024 * 1024,
  createdAt: '2026-10-26T08:20:00Z',
  isCurrent,
  storeCount,
});

describe('ReleasesCard', () => {
  it('loja baixando a versão nova aparece com o andamento, não como "nada a atualizar"', () => {
    const store = storeSummary({
      agentVersion: '0.6.27+65cb030',
      update: {
        version: '0.6.29',
        stage: 'downloading',
        doneBytes: 52 * 1024 * 1024,
        totalBytes: 109 * 1024 * 1024,
        error: null,
        at: '2026-10-26T16:00:00Z',
      },
    });
    renderScreen(<ReleasesCard />, [
      [keys.releases, [release('0.6.29', true, 0), release('0.6.27', false, 1)]],
      [keys.stores, [store]],
      [keys.github, { repository: null }],
    ]);

    expect(screen.getByText('1 loja está atualizando agora.')).toBeInTheDocument();
    expect(screen.getByText(/Baixando o agente 0.6.29… 52 MB de 109 MB/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Marumbi Centro' })).toHaveAttribute('href', '/lojas/1?aba=ajustes');
    expect(screen.getByRole('button', { name: 'Atualizar lojas' })).toBeDisabled();
    expect(screen.queryByText('Nenhuma loja conectada precisa de atualização.')).not.toBeInTheDocument();
  });

  it('loja numa versão anterior liga o "Atualizar lojas"', () => {
    renderScreen(<ReleasesCard />, [
      [keys.releases, [release('0.6.29', true, 0), release('0.6.27', false, 1)]],
      [keys.stores, [storeSummary({ agentVersion: '0.6.27+65cb030' })]],
      [keys.github, { repository: null }],
    ]);

    expect(screen.getByText('1 loja conectada está numa versão anterior à 0.6.29.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Atualizar lojas' })).toBeEnabled();
  });
});
