import type { DraftItem } from './playlistDraft';
import { anchorTime, assignRows, locate, tickStep, ticks } from './timeline';

const items: DraftItem[] = [
  { key: 'a', id: 'a', hash: 'h1', title: '001', artist: null, durationMs: 200_000 },
  { key: 'b', id: 'b', hash: 'h2', title: '002', artist: null, durationMs: 180_000 },
];

describe('timeline', () => {
  it('converte ponto da linha em música e "entra aos", arredondando para o segundo', () => {
    expect(locate(items, 61_400)).toEqual({ itemKey: 'a', offsetMs: 61_000 });
    expect(locate(items, 200_000)).toEqual({ itemKey: 'b', offsetMs: 0 });
    expect(locate(items, 260_600)).toEqual({ itemKey: 'b', offsetMs: 61_000 });
    expect(locate(items, -5_000)).toEqual({ itemKey: 'a', offsetMs: 0 });
    expect(locate([], 1_000)).toBeNull();
  });

  it('não deixa a oferta entrar nos últimos 5 s da música nem depois do fim', () => {
    expect(locate(items, 198_000)).toEqual({ itemKey: 'a', offsetMs: 195_000 });
    expect(locate(items, 999_000)).toEqual({ itemKey: 'b', offsetMs: 175_000 });
  });

  it('tempo da oferta presa desde o começo da playlist', () => {
    expect(anchorTime(items, { itemKey: 'b', offsetMs: 30_000 })).toBe(230_000);
    expect(anchorTime(items, { itemKey: 'sumiu', offsetMs: 0 })).toBeNull();
  });

  it('régua com marcas espaçadas conforme o zoom', () => {
    expect(tickStep(240)).toBe(30_000);
    expect(tickStep(60)).toBe(120_000);
    expect(tickStep(15)).toBe(300_000);
    expect(ticks(380_000, 60)).toEqual([0, 120_000, 240_000, 360_000]);
  });

  it('ofertas que se encostam vão para linhas diferentes', () => {
    const rows = assignRows([
      { key: 'x', start: 0, end: 15_000 },
      { key: 'y', start: 10_000, end: 20_000 },
      { key: 'z', start: 16_000, end: 30_000 },
    ]);
    expect([rows.get('x'), rows.get('y'), rows.get('z')]).toEqual([0, 1, 0]);
  });
});
