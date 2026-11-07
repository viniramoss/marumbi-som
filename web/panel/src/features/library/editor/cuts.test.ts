import { formatPrecise, normalizeCuts, parsePrecise, removedMs, rulerStep, skipTarget } from './cuts';

describe('trechos do editor de música (D34)', () => {
  it('ordena, junta os que se encostam e limita à duração, como o Hub', () => {
    const cuts = normalizeCuts(
      [
        { startMs: 30_000, endMs: 25_000 },
        { startMs: 10_000, endMs: 12_000 },
        { startMs: 11_000, endMs: 15_000 },
        { startMs: 15_100, endMs: 16_000 },
        { startMs: 40_000, endMs: 40_050 },
        { startMs: 58_000, endMs: 70_000 },
      ],
      60_000,
    );

    expect(cuts).toEqual([
      { startMs: 10_000, endMs: 16_000 },
      { startMs: 25_000, endMs: 30_000 },
      { startMs: 58_000, endMs: 60_000 },
    ]);
    expect(removedMs(cuts)).toBe(13_000);
  });

  it('sobra curta no começo entra no corte', () => {
    expect(normalizeCuts([{ startMs: 150, endMs: 5_000 }], 30_000)).toEqual([{ startMs: 0, endMs: 5_000 }]);
  });

  it('ao ouvir sem os trechos, pula para o fim do trecho', () => {
    const cuts = [{ startMs: 10_000, endMs: 20_000 }];
    expect(skipTarget(9_999, cuts)).toBeNull();
    expect(skipTarget(10_000, cuts)).toBe(20_000);
    expect(skipTarget(19_990, cuts)).toBeNull();
  });

  it('tempo com décimos, digitado de vários jeitos', () => {
    expect(formatPrecise(65_240)).toBe('1:05,2');
    expect(formatPrecise(0)).toBe('0:00,0');
    expect(parsePrecise('1:05,2')).toBe(65_200);
    expect(parsePrecise('1:05.2')).toBe(65_200);
    expect(parsePrecise(' 1:05 ')).toBe(65_000);
    expect(parsePrecise('65,2')).toBe(65_200);
    expect(parsePrecise('1:75')).toBeNull();
    expect(parsePrecise('abc')).toBeNull();
  });

  it('marcas da régua ficam espaçadas', () => {
    expect(rulerStep(100)).toBe(1_000);
    expect(rulerStep(10)).toBe(10_000);
    expect(rulerStep(0.5)).toBe(300_000);
  });
});
