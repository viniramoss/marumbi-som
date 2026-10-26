import type { DraftItem } from './playlistDraft';
import {
  anchorTime,
  assignRows,
  fitZoom,
  locate,
  minorTicks,
  nextZoom,
  snapToStart,
  tickStep,
  ticks,
} from './timeline';

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

  it('abre mostrando a playlist inteira e o zoom anda pelos níveis a partir dela', () => {
    // 38 min em 1140 px = 30 px por minuto.
    expect(fitZoom(38 * 60_000, 1140)).toBe(30);
    expect(fitZoom(0, 1140)).toBe(60);
    expect(fitZoom(38 * 60_000, 0)).toBe(60);
    expect(nextZoom(30, 1, 30)).toBe(60);
    expect(nextZoom(480, 1, 30)).toBeNull();
    expect(nextZoom(120, -1, 30)).toBe(60);
    expect(nextZoom(60, -1, 30)).toBeNull();
  });

  it('tracinhos menores entre as marcas e encaixe no começo da música', () => {
    // A 60 px por minuto as marcas são de 2 em 2 min e os tracinhos de 30 em 30 s.
    expect(minorTicks(240_000, 60)).toEqual([30_000, 60_000, 90_000, 150_000, 180_000, 210_000]);
    expect(minorTicks(600_000, 30)).toEqual([60_000, 120_000, 180_000, 240_000, 360_000, 420_000, 480_000, 540_000]);
    // A 60 px por minuto, 8 px são 8 s: 205 s encaixa no começo da segunda música (200 s), 210 s não.
    expect(snapToStart(items, 205_000, 60)).toBe(200_000);
    expect(snapToStart(items, 210_000, 60)).toBe(210_000);
    expect(snapToStart(items, 3_000, 60)).toBe(0);
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
