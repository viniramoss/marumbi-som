import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { keys } from '../../api/queries';
import { playlistSummary, storeDetail } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { ScheduleTab } from './ScheduleTab';

const playlists = [playlistSummary(), playlistSummary({ id: 2, name: 'Tarde' })];

function renderTab() {
  return renderScreen(<ScheduleTab detail={storeDetail()} />, [[keys.playlists, playlists]]);
}

describe('ScheduleTab', () => {
  it('mostra a agenda salva na lista e na grade, com 24:00 como meia-noite', () => {
    renderTab();

    const list = screen.getByRole('list', { name: 'Faixas da agenda' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByLabelText('Playlist da faixa 1')).toHaveValue('1');
    expect(screen.getByLabelText('Fim da faixa 1')).toHaveValue('00:00');
    expect(screen.getByText(/até a meia-noite/)).toBeInTheDocument();
    // Seg a Sáb: seis blocos na grade.
    expect(screen.getAllByTitle('Faixa 1: Manhã, 07:00–24:00')).toHaveLength(6);
    expect(screen.getByRole('button', { name: 'Salvar agenda' })).toBeDisabled();
  });

  it('faixa sobreposta aparece como erro e impede salvar', async () => {
    const user = userEvent.setup();
    renderTab();

    await user.click(screen.getByRole('button', { name: 'Adicionar faixa' }));
    await user.selectOptions(screen.getByLabelText('Playlist da faixa 2'), '2');

    expect(screen.getByText('Faixas 1 e 2 se sobrepõem na segunda.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar agenda' })).toBeDisabled();

    // Domingo não tem a faixa 1: deixar a faixa 2 só no domingo resolve.
    const days = screen.getByRole('group', { name: 'Dias da faixa 2' });
    for (const day of ['Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb']) {
      await user.click(within(days).getByRole('button', { name: day }));
    }
    await user.click(within(days).getByRole('button', { name: 'Dom' }));

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar agenda' })).toBeEnabled();
  });
});
