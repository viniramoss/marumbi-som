import { maxAnchorOffset, startTimes, type DraftAnchor, type DraftItem } from './playlistDraft';

/** Zoom da linha do tempo, em pixels por minuto (D30). A linha abre ajustada para mostrar a playlist inteira. */
export const zoomLevels = [15, 30, 60, 120, 240, 480] as const;
/** Usado quando ainda não dá para medir a largura da tela. */
export const defaultZoom = 60;
const minZoom = 2;

/** Zoom que faz a playlist inteira caber na largura disponível. */
export function fitZoom(totalMs: number, widthPx: number): number {
  if (widthPx <= 0 || totalMs <= 0) return defaultZoom;
  return Math.max(minZoom, widthPx / (totalMs / 60_000));
}

/** Próximo zoom ao aproximar (+) ou afastar (−); afastar além da playlist inteira volta para ela (nulo). */
export function nextZoom(current: number, direction: 1 | -1, fit: number): number | null {
  if (direction > 0) return zoomLevels.find((level) => level > current * 1.15) ?? null;
  const lower = [...zoomLevels].reverse().find((level) => level < current / 1.15);
  return lower === undefined || lower <= fit ? null : lower;
}

/** Intervalos possíveis entre as marcas da régua; a régua usa o menor que deixe espaço para o rótulo. */
const tickSteps = [10_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000];
const minTickPx = 72;

export const pxPerMs = (pxPerMinute: number) => pxPerMinute / 60_000;

export function tickStep(pxPerMinute: number): number {
  const scale = pxPerMs(pxPerMinute);
  return tickSteps.find((step) => step * scale >= minTickPx) ?? tickSteps[tickSteps.length - 1]!;
}

/** Marcas da régua de 0 até o fim da playlist. */
export function ticks(totalMs: number, pxPerMinute: number): number[] {
  return marks(totalMs, tickStep(pxPerMinute));
}

const minorSteps = [5_000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000];

/** Tracinhos menores entre as marcas, em intervalos redondos e com pelo menos 20 px entre eles. */
export function minorTicks(totalMs: number, pxPerMinute: number): number[] {
  const step = tickStep(pxPerMinute);
  const scale = pxPerMs(pxPerMinute);
  const minor = minorSteps.find((m) => m < step && step % m === 0 && m * scale >= 20);
  return minor ? marks(totalMs, minor).filter((at) => at % step !== 0) : [];
}

function marks(totalMs: number, step: number): number[] {
  const list: number[] = [];
  for (let at = 0; at <= totalMs; at += step) list.push(at);
  return list;
}

/** Perto do começo de uma música (até `snapPx` na tela), a oferta encaixa no 0:00 dela. */
export function snapToStart(items: readonly DraftItem[], timeMs: number, pxPerMinute: number, snapPx = 8): number {
  const limit = snapPx / pxPerMs(pxPerMinute);
  const near = startTimes(items).find((start) => Math.abs(start - timeMs) <= limit);
  return near ?? timeMs;
}

/** Onde a oferta entra, em tempo desde o começo da playlist; nulo se a música dela não está mais na lista. */
export function anchorTime(
  items: readonly DraftItem[],
  anchor: Pick<DraftAnchor, 'itemKey' | 'offsetMs'>,
): number | null {
  const starts = startTimes(items);
  const index = items.findIndex((item) => item.key === anchor.itemKey);
  return index < 0 ? null : starts[index]! + (anchor.offsetMs ?? 0);
}

/**
 * Ponto da linha do tempo → música e "entra aos". O tempo é arredondado para o segundo e fica entre 0:00 e 5 s antes do
 * fim da música (a mesma margem do Hub e do agente).
 */
export function locate(items: readonly DraftItem[], timeMs: number): { itemKey: string; offsetMs: number } | null {
  if (items.length === 0) return null;
  const starts = startTimes(items);
  const at = Math.max(0, timeMs);
  let index = starts.findIndex((start, i) => at < start + items[i]!.durationMs);
  if (index < 0) index = items.length - 1;
  const item = items[index]!;
  const offset = Math.round((at - starts[index]!) / 1000) * 1000;
  return { itemKey: item.key, offsetMs: Math.min(Math.max(0, offset), maxAnchorOffset(item.durationMs)) };
}

/** Linha de cada bloco de oferta, para blocos que se encostam não ficarem um em cima do outro. */
export function assignRows(blocks: readonly { key: string; start: number; end: number }[]): Map<string, number> {
  const rows = new Map<string, number>();
  const rowEnds: number[] = [];
  for (const block of [...blocks].sort((a, b) => a.start - b.start)) {
    let row = rowEnds.findIndex((end) => end <= block.start);
    if (row < 0) {
      row = rowEnds.length;
      rowEnds.push(block.end);
    } else {
      rowEnds[row] = block.end;
    }
    rows.set(block.key, row);
  }
  return rows;
}
