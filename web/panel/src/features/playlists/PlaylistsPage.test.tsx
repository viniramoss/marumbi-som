import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { keys } from '../../api/queries';
import { playlistSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { PlaylistsPage } from './PlaylistsPage';

describe('PlaylistsPage', () => {
  it('clicar em qualquer parte da linha abre o editor da playlist', async () => {
    const user = userEvent.setup();
    const { router } = renderScreen(<PlaylistsPage />, [[keys.playlists, [playlistSummary({ id: 7 })]]]);

    await user.click(screen.getByText('Fora da agenda'));

    expect(router.state.location.pathname).toBe('/playlists/7');
  });
});
