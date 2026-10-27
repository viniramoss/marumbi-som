import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Modal } from './Modal';

function Screen({ tick, onClose }: { tick: number; onClose: () => void }) {
  // Função nova a cada render, como nas telas que se atualizam ao vivo.
  return (
    <Modal title="Adicionar músicas" onClose={() => onClose()}>
      <input aria-label="Buscar" />
      <input type="checkbox" aria-label={`Marcar 040 (${tick})`} />
    </Modal>
  );
}

describe('Modal', () => {
  it('foca o primeiro campo só ao abrir: atualizar a tela atrás não tira o foco nem rola a lista para cima', async () => {
    const onClose = vi.fn();
    const { rerender } = render(<Screen tick={0} onClose={onClose} />);
    expect(screen.getByLabelText('Buscar')).toHaveFocus();

    await userEvent.click(screen.getByLabelText('Marcar 040 (0)'));
    rerender(<Screen tick={1} onClose={onClose} />);
    expect(screen.getByLabelText('Marcar 040 (1)')).toHaveFocus();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
