import { screen } from '@testing-library/react';
import { keys } from '../../api/queries';
import type { ReleaseInfo } from '../../api/types';
import { storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { AgentUpdateNotice } from './AgentUpdateNotice';

const release: ReleaseInfo = {
  version: '0.6.40',
  sha256: 'a'.repeat(64),
  sizeBytes: 110_000_000,
  createdAt: '2026-10-24T13:00:00Z',
  isCurrent: true,
  storeCount: 0,
};

describe('AgentUpdateNotice', () => {
  it('avisa a versão nova e leva para Configurações', () => {
    const stores = [
      storeSummary({ agentVersion: '0.6.30+abc1234' }),
      storeSummary({ id: 2, name: 'Loja 2', agentVersion: '0.6.40' }),
    ];
    renderScreen(<AgentUpdateNotice stores={stores} />, [[keys.releases, [release]]]);

    const link = screen.getByRole('link', { name: /Versão 0\.6\.40 do agente disponível/ });
    expect(link).toHaveAttribute('href', '/configuracoes');
    expect(link).toHaveTextContent('1 loja numa versão anterior (Marumbi Centro).');
  });

  it('não aparece com as lojas em dia ou sem versão para distribuição', () => {
    const { unmount } = renderScreen(<AgentUpdateNotice stores={[storeSummary({ agentVersion: '0.6.40' })]} />, [
      [keys.releases, [release]],
    ]);
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    unmount();

    renderScreen(<AgentUpdateNotice stores={[storeSummary({ agentVersion: '0.6.30' })]} />, [[keys.releases, []]]);
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});
