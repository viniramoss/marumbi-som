import type { HistoryEntry } from '../../api/types';

export type Period = 'hoje' | 'semana';

/** Início do período no horário do navegador: hoje 00:00, ou 00:00 de seis dias atrás (7 dias contando hoje). */
export function periodStart(period: Period, now: Date): string {
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  if (period === 'semana') start.setDate(start.getDate() - 6);
  return start.toISOString();
}

/** Agrupa por dia, do mais recente para o mais antigo (o Hub já devolve em ordem decrescente). */
export function groupByDay(entries: readonly HistoryEntry[]): { day: string; entries: HistoryEntry[] }[] {
  const groups: { day: string; entries: HistoryEntry[] }[] = [];
  for (const entry of entries) {
    const day = new Date(entry.startedAt).toDateString();
    const last = groups.at(-1);
    if (last?.day === day) last.entries.push(entry);
    else groups.push({ day, entries: [entry] });
  }
  return groups;
}

/** "12 músicas · 3 ofertas" para o cabeçalho de cada dia. */
export function dayCounts(entries: readonly HistoryEntry[]): string {
  const offers = entries.filter((e) => e.kind === 'offer').length;
  const music = entries.length - offers;
  const parts = [`${music} ${music === 1 ? 'música' : 'músicas'}`];
  if (offers > 0) parts.push(`${offers} ${offers === 1 ? 'oferta' : 'ofertas'}`);
  return parts.join(' · ');
}
