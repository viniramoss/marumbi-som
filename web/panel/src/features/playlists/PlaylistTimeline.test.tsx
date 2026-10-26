import { createEvent, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { OfferSummary } from '../../api/types';
import type { DraftAnchor, DraftItem } from './playlistDraft';
import { OfferDrag, PlaylistTimeline, type PlayingNow } from './PlaylistTimeline';

// No jsdom a faixa começa em x = 0 sem rolagem; com o zoom inicial (60 px por minuto) 1 px é 1 s, depois da margem de 12 px.
const at = (seconds: number) => 12 + seconds;

const items: DraftItem[] = [
  { key: 'a', id: 'a', hash: 'h-a', title: '001', artist: null, durationMs: 180_000 },
  { key: 'b', id: 'b', hash: 'h-b', title: '002', artist: null, durationMs: 240_000 },
];

const anchor: DraftAnchor = {
  key: 'ancora-1',
  offerId: 5,
  offerName: 'Picanha',
  itemKey: 'a',
  kind: 'during',
  offsetMs: 30_000,
};

const offer = { id: 5, name: 'Picanha', durationMs: 12_000 } as OfferSummary;

function renderTimeline({ shuffle = false, playing = [] as PlayingNow[] } = {}) {
  const props = { onMove: vi.fn(), onDropOffer: vi.fn(), onEdit: vi.fn() };
  render(
    <PlaylistTimeline
      items={items}
      anchors={[anchor]}
      offers={[offer]}
      shuffle={shuffle}
      playing={playing}
      {...props}
    />,
  );
  return props;
}

/** Dispara o evento com clientX (o jsdom não tem DragEvent e o testing-library perderia a coordenada). */
function fireAt(
  element: Element,
  type: 'dragOver' | 'drop' | 'pointerDown' | 'pointerMove' | 'pointerUp',
  clientX: number,
  init: object = {},
) {
  const event = createEvent[type](element, init);
  Object.defineProperty(event, 'clientX', { value: clientX });
  fireEvent(element, event);
}

const offerTransfer = (id: string) => ({
  types: [OfferDrag],
  getData: (type: string) => (type === OfferDrag ? id : ''),
});

describe('PlaylistTimeline', () => {
  it('soltar uma oferta num ponto prende ela na música daquele ponto, no segundo exato', () => {
    const { onDropOffer } = renderTimeline();
    const track = screen.getByTestId('timeline').firstElementChild!;

    fireAt(track, 'dragOver', at(200), { dataTransfer: offerTransfer('5') });
    expect(screen.getByText('002 · aos 0:20')).toBeInTheDocument();
    fireAt(track, 'drop', at(200), { dataTransfer: offerTransfer('5') });

    expect(onDropOffer).toHaveBeenCalledWith(5, { itemKey: 'b', offsetMs: 20_000 });
  });

  it('arrastar o bloco muda o "entra aos" sem abrir o ajuste', () => {
    const { onMove, onEdit } = renderTimeline();
    const block = screen.getByRole('button', { name: 'Picanha: entra aos 0:30 de 001' });

    fireAt(block, 'pointerDown', 100, { button: 0, pointerId: 1 });
    fireAt(block, 'pointerMove', 130, { pointerId: 1 });
    expect(screen.getByText('001 · aos 1:00')).toBeInTheDocument();
    fireAt(block, 'pointerUp', 130, { pointerId: 1 });
    fireEvent.click(block);

    expect(onMove).toHaveBeenCalledWith(anchor, { itemKey: 'a', offsetMs: 60_000 });
    expect(onEdit).not.toHaveBeenCalled();
  });

  it('arrastar além do fim da música passa para a próxima', () => {
    const { onMove } = renderTimeline();
    const block = screen.getByRole('button', { name: 'Picanha: entra aos 0:30 de 001' });

    fireAt(block, 'pointerDown', 100, { button: 0, pointerId: 1 });
    fireAt(block, 'pointerMove', 300, { pointerId: 1 });
    fireAt(block, 'pointerUp', 300, { pointerId: 1 });

    expect(onMove).toHaveBeenCalledWith(anchor, { itemKey: 'b', offsetMs: 50_000 });
  });

  it('clique abre o ajuste e as setas movem 1 s (com Shift, 10 s)', async () => {
    const user = userEvent.setup();
    const { onMove, onEdit } = renderTimeline();
    const block = screen.getByRole('button', { name: 'Picanha: entra aos 0:30 de 001' });

    await user.click(block);
    expect(onEdit).toHaveBeenCalledWith(anchor);

    await user.keyboard('{ArrowRight}');
    expect(onMove).toHaveBeenLastCalledWith(anchor, { itemKey: 'a', offsetMs: 31_000 });
    await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    expect(onMove).toHaveBeenLastCalledWith(anchor, { itemKey: 'a', offsetMs: 20_000 });
  });

  it('marca a música tocando agora e o ponto em que ela está', () => {
    renderTimeline({ playing: [{ storeName: 'Loja 1', hash: 'h-b', positionMs: 15_000 }] });

    expect(screen.getByText('Loja 1')).toBeInTheDocument();
    expect(screen.getByTitle('2. 002 (4:00)')).toHaveClass('bg-brand-50');
    expect(screen.getByTitle('1. 001 (3:00)')).not.toHaveClass('bg-brand-50');
  });

  it('no aleatório não mostra ofertas presas nem aceita soltar', () => {
    const { onDropOffer } = renderTimeline({ shuffle: true });
    const track = screen.getByTestId('timeline').firstElementChild!;

    expect(screen.queryByRole('button', { name: /Picanha/ })).not.toBeInTheDocument();
    expect(screen.getByText('Sem ofertas presas no aleatório.')).toBeInTheDocument();
    fireAt(track, 'drop', at(200), { dataTransfer: offerTransfer('5') });
    expect(onDropOffer).not.toHaveBeenCalled();
  });
});
