import type { ScheduleInput } from '../../api/types';
import { weekdayShort } from '../../lib/format';

/** Faixa da agenda sendo editada. `end` "00:00" é meia-noite (o Hub recebe "24:00"). */
export interface ScheduleRow {
  key: string;
  days: number[];
  start: string;
  end: string;
  playlistId: number | null;
}

/** Ordem dos dias na tela: a semana da loja começa na segunda. */
export const weekOrder = [1, 2, 3, 4, 5, 6, 0] as const;

/** "no domingo", "na segunda"… para as mensagens. */
export const weekdayOn = [
  'no domingo',
  'na segunda',
  'na terça',
  'na quarta',
  'na quinta',
  'na sexta',
  'no sábado',
] as const;

const DayMinutes = 1440;
const WeekMinutes = 7 * DayMinutes;
const timePattern = /^([01]\d|2[0-3]):[0-5]\d$/;

let counter = 0;
export const newRowKey = () => `faixa-${++counter}`;

export function rowsFrom(schedules: readonly ScheduleInput[]): ScheduleRow[] {
  return schedules.map((s) => ({
    key: newRowKey(),
    days: [...s.days].sort((a, b) => a - b),
    start: s.start,
    end: s.end === '24:00' ? '00:00' : s.end,
    playlistId: s.playlistId,
  }));
}

export function newRow(previous?: ScheduleRow): ScheduleRow {
  return {
    key: newRowKey(),
    days: previous ? [...previous.days] : [1, 2, 3, 4, 5, 6],
    start: previous?.end && previous.end !== '00:00' ? previous.end : '08:00',
    end: previous ? '00:00' : '20:00',
    playlistId: null,
  };
}

/** Corpo do PUT /schedules. Só chamar depois de `validateRows` sem erros. */
export function toScheduleInput(rows: readonly ScheduleRow[]): ScheduleInput[] {
  return rows.map((row) => ({
    days: [...row.days].sort((a, b) => a - b),
    start: row.start,
    end: row.end === '00:00' ? '24:00' : row.end,
    playlistId: row.playlistId ?? 0,
  }));
}

export function toMinutes(time: string): number {
  const [hours, minutes] = time.split(':').map(Number);
  return hours! * 60 + minutes!;
}

/** Duração da faixa em minutos. Fim menor ou igual ao início atravessa a meia-noite (igual = 24 h), como no Hub. */
export function durationMinutes(row: Pick<ScheduleRow, 'start' | 'end'>): number {
  const start = toMinutes(row.start);
  const end = toMinutes(row.end);
  return end > start ? end - start : DayMinutes - start + end;
}

export const crossesMidnight = (row: Pick<ScheduleRow, 'start' | 'end'>) =>
  row.end !== '00:00' && toMinutes(row.end) <= toMinutes(row.start);

/** Intervalos da faixa na semana, em minutos desde domingo 00:00 (podem passar do fim da semana). */
function weekRanges(row: ScheduleRow): { day: number; from: number; to: number }[] {
  const start = toMinutes(row.start);
  return row.days.map((day) => ({
    day,
    from: day * DayMinutes + start,
    to: day * DayMinutes + start + durationMinutes(row),
  }));
}

export interface ScheduleProblem {
  /** Índices das faixas com problema, para destacar na tela. */
  rows: number[];
  message: string;
}

/**
 * Mesmas regras do Hub (PUT /api/stores/{id}/schedules): playlist e dias obrigatórios e nenhuma sobreposição no
 * mesmo dia, contando a faixa que passa da meia-noite de sábado para domingo.
 */
export function validateRows(rows: readonly ScheduleRow[]): ScheduleProblem[] {
  const problems: ScheduleProblem[] = [];
  rows.forEach((row, index) => {
    const label = `Faixa ${index + 1}`;
    if (row.playlistId === null) problems.push({ rows: [index], message: `${label}: escolha a playlist.` });
    if (row.days.length === 0) problems.push({ rows: [index], message: `${label}: marque pelo menos um dia.` });
    if (!timePattern.test(row.start) || !timePattern.test(row.end)) {
      problems.push({ rows: [index], message: `${label}: horário inválido.` });
    }
  });
  if (problems.length > 0) return problems;

  const ranges = rows.flatMap((row, index) => weekRanges(row).map((range) => ({ ...range, index })));
  // Uma faixa de sábado que passa da meia-noite encosta no domingo seguinte.
  ranges.push(
    ...ranges
      .filter((r) => r.to > WeekMinutes)
      .map((r) => ({ ...r, from: r.from - WeekMinutes, to: r.to - WeekMinutes })),
  );
  const seen = new Set<string>();
  for (let i = 0; i < ranges.length; i++) {
    for (let j = i + 1; j < ranges.length; j++) {
      const a = ranges[i]!;
      const b = ranges[j]!;
      if (a.index === b.index || a.from >= b.to || b.from >= a.to) continue;
      const [first, second] = a.index < b.index ? [a, b] : [b, a];
      const pair = `${first.index}-${second.index}`;
      if (seen.has(pair)) continue;
      seen.add(pair);
      const day = Math.floor(Math.max(a.from, b.from) / DayMinutes) % 7;
      problems.push({
        rows: [first.index, second.index],
        message: `Faixas ${first.index + 1} e ${second.index + 1} se sobrepõem ${weekdayOn[day]}.`,
      });
    }
  }
  return problems;
}

export interface GridBlock {
  index: number;
  /** Dia da coluna (0 = domingo). */
  day: number;
  /** Minutos desde 00:00 do dia da coluna. */
  from: number;
  to: number;
  /** Pedaço que continua da véspera (faixa que passou da meia-noite). */
  continued: boolean;
}

/** Blocos da grade semanal: a faixa que passa da meia-noite vira dois blocos, um em cada dia. */
export function gridBlocks(rows: readonly ScheduleRow[]): GridBlock[] {
  return rows.flatMap((row, index) =>
    row.days.flatMap((day) => {
      const from = toMinutes(row.start);
      const to = from + durationMinutes(row);
      if (to <= DayMinutes) return [{ index, day, from, to, continued: false }];
      return [
        { index, day, from, to: DayMinutes, continued: false },
        { index, day: (day + 1) % 7, from: 0, to: to - DayMinutes, continued: true },
      ];
    }),
  );
}

/** Faixa de horas mostrada na grade: das 6 h às 22 h, ampliada para caber todas as faixas. */
export function gridHours(blocks: readonly GridBlock[]): { from: number; to: number } {
  let from = 6;
  let to = 22;
  for (const block of blocks) {
    from = Math.min(from, Math.floor(block.from / 60));
    to = Math.max(to, Math.ceil(block.to / 60));
  }
  return { from, to };
}

export function rowsEqual(a: readonly ScheduleRow[], b: readonly ScheduleRow[]): boolean {
  if (a.length !== b.length) return false;
  return a.every((row, i) => {
    const other = b[i]!;
    return (
      row.start === other.start &&
      row.end === other.end &&
      row.playlistId === other.playlistId &&
      row.days.length === other.days.length &&
      [...row.days].sort().join() === [...other.days].sort().join()
    );
  });
}

export function describeRow(row: ScheduleRow): string {
  const days = weekOrder.filter((d) => row.days.includes(d)).map((d) => weekdayShort[d]);
  return `${days.join(', ')} · ${row.start}–${row.end === '00:00' ? '24:00' : row.end}`;
}
