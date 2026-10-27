import { screen } from '@testing-library/react';
import { keys } from '../../api/queries';
import type { HubUpdateStatus } from '../../api/types';
import { renderScreen } from '../../test/render';
import { HubUpdateCard } from './HubUpdateCard';

const status = (overrides: Partial<HubUpdateStatus> = {}): HubUpdateStatus => ({
  currentVersion: '0.6.41',
  supported: true,
  updaterInstalled: true,
  available: null,
  download: null,
  message: null,
  problem: false,
  searching: false,
  autoUpdate: true,
  autoUpdateTime: '03:00',
  pending: null,
  last: null,
  ...overrides,
});

const available = { version: '0.6.42', sizeBytes: 96 * 1024 * 1024, downloadedAt: '2026-10-26T12:00:00Z' };

describe('HubUpdateCard', () => {
  it('versão nova baixada liga o "Atualizar o Hub agora" e mostra a madrugada ligada', () => {
    renderScreen(<HubUpdateCard />, [[keys.hubUpdate, status({ available })]]);

    expect(screen.getByText('0.6.41')).toBeInTheDocument();
    expect(screen.getByText('0.6.42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Atualizar o Hub agora' })).toBeEnabled();
    expect(screen.getByRole('checkbox', { name: /Atualizar sozinho de madrugada/ })).toBeChecked();
    expect(screen.getByText(/entre 03:00 e 04:00, uma vez por versão/)).toBeInTheDocument();
  });

  it('sem versão baixada oferece "Buscar atualizações", que procura e já instala (D35)', () => {
    renderScreen(<HubUpdateCard />, [
      [keys.hubUpdate, status({ message: 'O Hub já está na versão mais nova do GitHub (0.6.41).' })],
    ]);

    expect(screen.getByRole('button', { name: 'Buscar atualizações' })).toBeEnabled();
    expect(screen.getByText(/se tiver versão nova, já instala/)).toBeInTheDocument();
    expect(screen.getByText('O Hub já está na versão mais nova do GitHub (0.6.41).')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Atualizar o Hub agora' })).not.toBeInTheDocument();
  });

  it('procurando mostra a barra no lugar da mensagem antiga', () => {
    renderScreen(<HubUpdateCard />, [[keys.hubUpdate, status({ searching: true, message: null })]]);

    expect(screen.getByText('Procurando versão nova no GitHub…')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Procurando versão nova do Hub no GitHub' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Buscar atualizações/ })).toBeDisabled();
  });

  it('sem o atualizador instalado, pede o install-hub.ps1 e não deixa pedir a troca', () => {
    renderScreen(<HubUpdateCard />, [[keys.hubUpdate, status({ available, updaterInstalled: false })]]);

    expect(screen.getByText(/O atualizador ainda não está instalado neste PC/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Atualizar o Hub agora' })).toBeDisabled();
  });

  it('pedido esperando o atualizador mostra o andamento, o cancelar e o aviso de atraso', () => {
    renderScreen(<HubUpdateCard />, [
      [
        keys.hubUpdate,
        status({
          available,
          pending: {
            version: '0.6.42',
            requestedAt: '2026-10-26T06:00:00Z',
            automatic: true,
            stage: 'waiting',
            updaterLate: true,
          },
        }),
      ],
    ]);

    expect(screen.getByText(/Atualização para a versão 0.6.42 pedida de madrugada/)).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Atualizando o Hub para a versão 0.6.42' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeEnabled();
    expect(screen.getByText(/O atualizador ainda não pegou o pedido/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Atualizar o Hub agora' })).not.toBeInTheDocument();
  });

  it('instalando não deixa cancelar', () => {
    renderScreen(<HubUpdateCard />, [
      [
        keys.hubUpdate,
        status({
          available,
          pending: {
            version: '0.6.42',
            requestedAt: '2026-10-26T06:00:00Z',
            automatic: false,
            stage: 'installing',
            updaterLate: false,
          },
        }),
      ],
    ]);

    expect(screen.getByText('Instalando a versão 0.6.42…')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancelar' })).not.toBeInTheDocument();
  });

  it('versão que não subiu aparece como volta automática, com o motivo', () => {
    renderScreen(<HubUpdateCard />, [
      [
        keys.hubUpdate,
        status({
          last: {
            version: '0.6.42',
            previousVersion: '0.6.41',
            outcome: 'rolledBack',
            error: 'O serviço não confirmou em 2 minutos.',
            at: '2026-10-26T06:03:00Z',
            automatic: true,
          },
        }),
      ],
    ]);

    expect(
      screen.getByText(/a versão 0.6.42 não subiu e o Hub voltou sozinho para a 0.6.41, com o banco como estava/),
    ).toBeInTheDocument();
    expect(screen.getByText(/Motivo: O serviço não confirmou em 2 minutos./)).toBeInTheDocument();
    expect(screen.getByText('Nenhuma versão mais nova')).toBeInTheDocument();
  });

  it('Hub fora da instalação padrão explica que não se atualiza sozinho', () => {
    renderScreen(<HubUpdateCard />, [[keys.hubUpdate, status({ supported: false, updaterInstalled: false })]]);

    expect(screen.getByText(/não está na instalação padrão/)).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Enviar pacote/ })).not.toBeInTheDocument();
  });
});
