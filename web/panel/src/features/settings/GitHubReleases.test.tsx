import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { keys } from '../../api/queries';
import type { GitHubStatus } from '../../api/types';
import { renderScreen } from '../../test/render';
import { GitHubReleases } from './GitHubReleases';

function status(overrides: Partial<GitHubStatus> = {}): GitHubStatus {
  return {
    repository: 'viniramoss/marumbi-som',
    tokenConfigured: true,
    checkIntervalHours: 6,
    checkedAt: '2026-10-24T13:00:00Z',
    message: 'A versão mais nova do GitHub (0.6.40) já está no Hub.',
    problem: false,
    checking: false,
    download: null,
    ...overrides,
  };
}

describe('GitHubReleases', () => {
  it('mostra a barra do download da versão nova', () => {
    renderScreen(<GitHubReleases />, [
      [
        keys.github,
        status({
          checking: true,
          download: { version: '0.6.41', doneBytes: 50 * 1024 * 1024, totalBytes: 100 * 1024 * 1024 },
        }),
      ],
    ]);

    expect(screen.getByText(/Baixando a versão 0\.6\.41/)).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Baixando a versão 0.6.41 do GitHub' })).toHaveAttribute(
      'aria-valuenow',
      '50',
    );
    expect(screen.getByRole('button', { name: /Procurar agora/ })).toBeDisabled();
  });

  it('sem token pede o token e mostra o motivo da recusa', async () => {
    const user = userEvent.setup();
    renderScreen(<GitHubReleases />, [
      [
        keys.github,
        status({
          tokenConfigured: false,
          problem: true,
          message:
            'O GitHub não mostrou o repositório. Como ele é privado, cadastre o token do GitHub em Configurações.',
        }),
      ],
    ]);

    expect(screen.getByText(/cadastre o token do GitHub/)).toBeInTheDocument();
    const save = screen.getByRole('button', { name: 'Salvar token' });
    expect(save).toBeDisabled();
    await user.type(screen.getByLabelText(/Token do GitHub/), 'github_pat_abc');
    expect(save).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Cancelar' })).not.toBeInTheDocument();
  });

  it('com token cadastrado não mostra o campo até pedir para trocar', async () => {
    const user = userEvent.setup();
    renderScreen(<GitHubReleases />, [[keys.github, status()]]);

    expect(screen.getByText('Token cadastrado')).toBeInTheDocument();
    expect(screen.getByText('A versão mais nova do GitHub (0.6.40) já está no Hub.')).toBeInTheDocument();
    expect(screen.queryByLabelText(/Token do GitHub/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Trocar token/ }));
    expect(screen.getByLabelText(/Token do GitHub/)).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(screen.getByText('Token cadastrado')).toBeInTheDocument();
  });

  it('procura desligada quando o Hub não tem repositório', () => {
    renderScreen(<GitHubReleases />, [[keys.github, status({ repository: null })]]);

    expect(screen.getByText(/está desligada neste Hub/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Procurar agora/ })).not.toBeInTheDocument();
  });
});
