import { useState } from 'react';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { sendCommand } from '../../api/queries';
import { storeDetail, storeSummary } from '../../test/fixtures';
import { renderScreen } from '../../test/render';
import { LiveTab } from './LiveTab';

vi.mock('../../api/queries', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../api/queries')>()),
  sendCommand: vi.fn(),
}));

/** Loja reiniciando o áudio; o botão "caixa voltou" faz o status mudar como chegaria pelo WebSocket. */
function Restarting() {
  const [bluetooth, setBluetooth] = useState('restartingAudio');
  return (
    <>
      <button onClick={() => setBluetooth('connected')}>caixa voltou</button>
      <LiveTab detail={storeDetail({ status: storeSummary({ bluetooth }) })} />
    </>
  );
}

describe('LiveTab: reiniciar áudio (D37)', () => {
  beforeEach(() => {
    vi.mocked(sendCommand).mockReset();
    vi.mocked(sendCommand).mockResolvedValue({ id: 1, result: 'ok', error: null });
  });

  it('pede confirmação antes de mandar o comando', async () => {
    const user = userEvent.setup();
    renderScreen(<LiveTab detail={storeDetail()} />);

    await user.click(screen.getByRole('button', { name: 'Reiniciar áudio' }));
    const dialog = screen.getByRole('dialog', { name: 'Reiniciar o áudio da loja?' });
    expect(within(dialog).getByText(/sem reiniciar o PC/)).toBeInTheDocument();
    expect(sendCommand).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole('button', { name: 'Reiniciar áudio' }));

    expect(sendCommand).toHaveBeenCalledWith(1, { command: 'restartAudio' });
    expect(await screen.findByText(/A loja está reiniciando o áudio/)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('mostra a barra em movimento enquanto reinicia e avisa quando a caixa volta', async () => {
    const user = userEvent.setup();
    renderScreen(<Restarting />);

    expect(screen.getByRole('progressbar', { name: 'Reiniciando o áudio da loja' })).toBeInTheDocument();
    expect(screen.getByText('Reiniciando o áudio…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reiniciar áudio' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'caixa voltou' }));

    expect(await screen.findByText('Áudio reiniciado e caixa conectada.')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar', { name: 'Reiniciando o áudio da loja' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reiniciar áudio' })).toBeEnabled();
  });
});
