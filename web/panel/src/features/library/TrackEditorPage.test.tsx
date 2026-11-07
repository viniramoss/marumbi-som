import { fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Track } from '../../api/types';
import { renderScreen } from '../../test/render';
import { TrackEditorView } from './TrackEditorPage';

const track = (overrides: Partial<Track> = {}): Track => ({
  hash: 'h1',
  kind: 'music',
  number: 19,
  title: '019',
  artist: null,
  durationMs: 100_000,
  sizeBytes: 2_000_000,
  loudnessLufs: -12,
  originalName: 'Forró da Picanha.mp3',
  createdAt: '2026-10-26T12:00:00Z',
  playlists: [],
  edited: false,
  removedMs: 0,
  ...overrides,
});

// 100 s de onda (um pico a cada 10 ms).
const waveform = { peaks: new Float32Array(10_000).fill(0.5), durationMs: 100_000 };

// O jsdom não tem PointerEvent nem tamanho de tela: a onda "mede" 1000 px, então 1 px = 100 ms.
class TestPointerEvent extends MouseEvent {
  pointerId = 1;
}

beforeAll(() => {
  window.PointerEvent ??= TestPointerEvent as unknown as typeof PointerEvent;
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
});

function drag(fromPx: number, toPx: number) {
  const wave = screen.getByTestId('waveform');
  vi.spyOn(wave, 'getBoundingClientRect').mockReturnValue({ left: 0, width: 1000, top: 0, height: 160 } as DOMRect);
  fireEvent.pointerDown(wave, { clientX: fromPx, button: 0 });
  fireEvent.pointerMove(wave, { clientX: toPx });
  fireEvent.pointerUp(wave, { clientX: toPx });
}

describe('TrackEditorView', () => {
  it('arrastar na onda marca o trecho; digitar acerta o início e o fim', async () => {
    renderScreen(<TrackEditorView track={track()} waveform={waveform} />);
    expect(screen.getByText('Nenhum trecho marcado.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar sem os trechos' })).toBeDisabled();

    drag(300, 420);

    expect(screen.getByLabelText('Início do trecho 1')).toHaveValue('0:30,0');
    expect(screen.getByLabelText('Fim do trecho 1')).toHaveValue('0:42,0');
    expect(screen.getByText('A música fica com 1:28 (tirando 0:12,0).')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Trecho 1: de 0:30,0 a 0:42,0' })).toBeInTheDocument();

    const end = screen.getByLabelText('Fim do trecho 1');
    await userEvent.clear(end);
    await userEvent.type(end, '0:50{Enter}');

    expect(screen.getByText('A música fica com 1:20 (tirando 0:20,0).')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar sem os trechos' })).toBeEnabled();

    await userEvent.click(screen.getByRole('button', { name: 'Desmarcar o trecho 1' }));
    expect(screen.getByText('Nenhum trecho marcado.')).toBeInTheDocument();
  });

  it('não deixa salvar se a música ficaria com menos de 30 s', () => {
    renderScreen(<TrackEditorView track={track()} waveform={waveform} />);

    drag(50, 900);

    expect(screen.getByText('Sem os trechos ficaria com 0:15; o mínimo é 0:30.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar sem os trechos' })).toBeDisabled();
  });

  it('música já editada oferece voltar para a original', async () => {
    renderScreen(<TrackEditorView track={track({ edited: true, removedMs: 5_000 })} waveform={waveform} />);

    expect(screen.getByText(/Esta música já foi editada: 0:05,0 a menos que o arquivo enviado/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Voltar para a original' }));

    expect(screen.getByRole('dialog', { name: 'Voltar para a original?' })).toBeInTheDocument();
    expect(screen.getByText(/volta a ser exatamente como foi enviada, com os 0:05,0/)).toBeInTheDocument();
  });
});
