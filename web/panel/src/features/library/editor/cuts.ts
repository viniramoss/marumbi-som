/** Trecho marcado para tirar da música (D34), em ms no tempo da música como ela está. */
export interface Cut {
  startMs: number;
  endMs: number;
}

/** Iguais aos do Hub (TrackCuts): clique sem arrastar não vira trecho; sobra curta entre trechos entra no corte. */
export const MinCutMs = 100;
export const MinKeptMs = 200;
/** A música editada precisa continuar com pelo menos isso (perfil de música da ingestão). */
export const MinDurationMs = 30_000;

/** Ordena, limita à duração e junta os trechos que se encostam, como o Hub faz ao salvar. */
export function normalizeCuts(cuts: readonly Cut[], durationMs: number): Cut[] {
  const sorted = cuts
    .map((c) => ({
      startMs: clamp(Math.min(c.startMs, c.endMs), 0, durationMs),
      endMs: clamp(Math.max(c.startMs, c.endMs), 0, durationMs),
    }))
    .filter((c) => c.endMs - c.startMs >= MinCutMs)
    .sort((a, b) => a.startMs - b.startMs);
  const result: Cut[] = [];
  for (const cut of sorted) {
    const last = result.at(-1);
    if (last && cut.startMs - last.endMs < MinKeptMs) last.endMs = Math.max(last.endMs, cut.endMs);
    else result.push({ ...cut });
  }
  const first = result[0];
  if (first && first.startMs < MinKeptMs) first.startMs = 0;
  const last = result.at(-1);
  if (last && durationMs - last.endMs < MinKeptMs) last.endMs = durationMs;
  return result;
}

export const removedMs = (cuts: readonly Cut[]) => cuts.reduce((sum, c) => sum + (c.endMs - c.startMs), 0);

/** Ao ouvir sem os trechos: se o ponto cai num trecho marcado, para onde pular (o fim dele). */
export function skipTarget(positionMs: number, cuts: readonly Cut[]): number | null {
  const cut = cuts.find((c) => positionMs >= c.startMs && positionMs < c.endMs - 30);
  return cut ? cut.endMs : null;
}

export const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/** 1:05,2 (minutos, segundos e décimos). */
export function formatPrecise(ms: number): string {
  const tenths = Math.round(Math.max(0, ms) / 100);
  const minutes = Math.floor(tenths / 600);
  const seconds = Math.floor((tenths % 600) / 10);
  return `${minutes}:${String(seconds).padStart(2, '0')},${tenths % 10}`;
}

/** Aceita "1:05,2", "1:05.2", "1:05" e "65,2" (segundos); devolve ms ou null. */
export function parsePrecise(text: string): number | null {
  const match = /^\s*(?:(\d+):)?(\d+)(?:[.,](\d))?\d*\s*$/.exec(text);
  if (!match) return null;
  const [, minutes, seconds, tenths] = match;
  if (minutes !== undefined && Number(seconds) >= 60) return null;
  return (Number(minutes ?? 0) * 60 + Number(seconds)) * 1000 + Number(tenths ?? 0) * 100;
}

/** Marcas da régua: de quanto em quanto tempo, para ficarem a pelo menos ~80 px uma da outra. */
export function rulerStep(pxPerSecond: number): number {
  const steps = [1, 2, 5, 10, 15, 30, 60, 120, 300];
  return (steps.find((s) => s * pxPerSecond >= 80) ?? 600) * 1000;
}
