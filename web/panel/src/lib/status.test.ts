import { storeSummary } from '../test/fixtures';
import { playerStatus } from './status';

describe('playerStatus', () => {
  it('segue o player quando a loja está conectada', () => {
    expect(playerStatus(storeSummary())).toEqual({ label: 'Tocando', tone: 'green' });
    expect(playerStatus(storeSummary({ player: 'paused' }))).toEqual({ label: 'Pausada', tone: 'amber' });
    expect(playerStatus(storeSummary({ online: false }))).toEqual({ label: 'Desconectada', tone: 'red' });
    expect(playerStatus(storeSummary({ enrolled: false }))).toEqual({ label: 'Não vinculada', tone: 'gray' });
  });

  it('em silêncio, diz se é falta de agenda ou fora do horário', () => {
    expect(playerStatus(storeSummary({ player: 'idle', hasSchedule: false, open: false }))).toEqual({
      label: 'Sem agenda',
      tone: 'amber',
    });
    expect(playerStatus(storeSummary({ player: 'idle', open: false }))).toEqual({
      label: 'Fora do horário',
      tone: 'gray',
    });
    expect(playerStatus(storeSummary({ player: 'idle' }))).toEqual({ label: 'Parada', tone: 'gray' });
  });
});
