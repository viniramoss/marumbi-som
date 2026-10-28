import {
  durationMinutes,
  gridBlocks,
  gridHours,
  rowsEqual,
  rowsFrom,
  toScheduleInput,
  validateRows,
  type ScheduleRow,
} from './schedule';

const row = (days: number[], start: string, end: string, playlistId: number | null = 1): ScheduleRow => ({
  key: `${days.join()}-${start}-${end}`,
  days,
  start,
  end,
  playlistId,
});

describe('validateRows (mesmas regras do Hub)', () => {
  it('aceita faixas que só encostam', () => {
    expect(validateRows([row([1, 2, 3], '08:00', '12:00'), row([1, 2, 3], '12:00', '18:00', 2)])).toEqual([]);
  });

  it('recusa sobreposição no mesmo dia e diz quais faixas', () => {
    const problems = validateRows([row([1, 2], '08:00', '13:00'), row([2, 3], '12:00', '18:00', 2)]);
    expect(problems).toEqual([{ rows: [0, 1], message: 'Faixas 1 e 2 se sobrepõem na terça.' }]);
  });

  it('a faixa que passa da meia-noite ocupa o começo do dia seguinte', () => {
    const problems = validateRows([row([5], '22:00', '02:00'), row([6], '01:00', '08:00', 2)]);
    expect(problems[0]?.message).toBe('Faixas 1 e 2 se sobrepõem no sábado.');
  });

  it('a madrugada de sábado para domingo vira a semana', () => {
    expect(validateRows([row([6], '22:00', '03:00'), row([0], '02:00', '10:00', 2)])).toHaveLength(1);
    expect(validateRows([row([6], '22:00', '03:00'), row([0], '03:00', '10:00', 2)])).toEqual([]);
  });

  it('dias diferentes não se sobrepõem', () => {
    expect(validateRows([row([1], '08:00', '18:00'), row([2], '08:00', '18:00', 2)])).toEqual([]);
  });

  it('exige playlist e dias', () => {
    expect(validateRows([row([1], '08:00', '12:00', null), row([], '13:00', '14:00')]).map((p) => p.message)).toEqual([
      'Faixa 1: escolha a playlist.',
      'Faixa 2: marque pelo menos um dia.',
    ]);
  });
});

describe('meia-noite', () => {
  it('"24:00" do Hub aparece como 00:00 e volta como 24:00', () => {
    const rows = rowsFrom([{ days: [3, 1], start: '07:00', end: '24:00', playlistId: 4 }]);
    expect(rows[0]).toMatchObject({ days: [1, 3], start: '07:00', end: '00:00', playlistId: 4 });
    expect(toScheduleInput(rows)).toEqual([{ days: [1, 3], start: '07:00', end: '24:00', playlistId: 4 }]);
  });

  it('duração atravessa a meia-noite e início igual ao fim dura 24 h', () => {
    expect(durationMinutes({ start: '07:00', end: '00:00' })).toBe(17 * 60);
    expect(durationMinutes({ start: '22:00', end: '02:00' })).toBe(4 * 60);
    expect(durationMinutes({ start: '00:00', end: '00:00' })).toBe(24 * 60);
  });
});

describe('grade semanal', () => {
  it('quebra a faixa da madrugada em dois blocos, um em cada dia', () => {
    const blocks = gridBlocks([row([6], '22:00', '02:00')]);
    expect(blocks).toEqual([
      { index: 0, day: 6, from: 22 * 60, to: 24 * 60, continued: false },
      { index: 0, day: 0, from: 0, to: 2 * 60, continued: true },
    ]);
    expect(gridHours(blocks)).toEqual({ from: 0, to: 24 });
  });

  it('mostra das 6 h às 22 h quando as faixas cabem', () => {
    expect(gridHours(gridBlocks([row([1], '08:00', '18:00')]))).toEqual({ from: 6, to: 22 });
  });
});

describe('rowsEqual', () => {
  it('ignora a chave e a ordem dos dias', () => {
    expect(rowsEqual([row([2, 1], '08:00', '12:00')], [{ ...row([1, 2], '08:00', '12:00'), key: 'outra' }])).toBe(true);
    expect(rowsEqual([row([1], '08:00', '12:00')], [row([1], '08:00', '13:00')])).toBe(false);
  });
});
