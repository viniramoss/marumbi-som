import { currentPositionMs } from './position';

const track = {
  hash: 'h1',
  title: 'Evidências',
  artist: null,
  durationMs: 240_000,
  positionMs: 60_000,
  positionAt: '2026-10-23T13:00:00Z',
};
const at = (iso: string) => Date.parse(iso);

describe('currentPositionMs', () => {
  it('anda a partir da última posição informada enquanto toca', () => {
    expect(currentPositionMs(track, 'playing', at('2026-10-23T13:00:10Z'))).toBe(70_000);
  });

  it('fica parada quando pausada', () => {
    expect(currentPositionMs(track, 'paused', at('2026-10-23T13:00:10Z'))).toBe(60_000);
  });

  it('nunca passa da duração', () => {
    expect(currentPositionMs(track, 'playing', at('2026-10-23T13:10:00Z'))).toBe(240_000);
  });

  it('sem música ou sem posição, não há barra', () => {
    expect(currentPositionMs(null, 'playing', at('2026-10-23T13:00:00Z'))).toBeNull();
    expect(currentPositionMs({ ...track, positionMs: null }, 'playing', at('2026-10-23T13:00:00Z'))).toBeNull();
  });
});
