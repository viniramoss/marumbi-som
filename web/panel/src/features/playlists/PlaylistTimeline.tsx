import { useLayoutEffect, useRef, useState } from 'react';
import { Lock, Megaphone, Volume2, ZoomIn, ZoomOut } from 'lucide-react';
import type { OfferSummary } from '../../api/types';
import { Card, CardHeader, IconButton } from '../../components/ui';
import { formatDuration } from '../../lib/format';
import { startTimes, totalDuration, type DraftAnchor, type DraftItem } from './playlistDraft';
import { anchorTime, assignRows, defaultZoom, locate, pxPerMs, ticks, zoomLevels } from './timeline';

/** Tipo do arrastar de uma oferta da coluna da direita (para a linha do tempo ou para cima de uma música). */
export const OfferDrag = 'application/x-marumbi-oferta';

/** Música tocando agora numa loja que usa esta playlist (D31 e). */
export interface PlayingNow {
  storeName: string;
  hash: string;
  positionMs: number | null;
}

export interface TimelineTarget {
  itemKey: string;
  offsetMs: number;
}

const pad = 12;
const rowHeight = 30;
const rulerHeight = 22;
const musicHeight = 52;
/** Oferta sem duração conhecida (removida do cadastro) aparece com 10 s. */
const fallbackOfferMs = 10_000;

/**
 * Linha do tempo da playlist (D30): as músicas em sequência como num editor de vídeo e as ofertas presas como blocos
 * arrastáveis. Soltar uma oferta da coluna da direita num ponto da linha a prende ali; arrastar um bloco muda a música e
 * o "entra aos"; clicar abre o ajuste fino. Setas do teclado movem o bloco selecionado 1 s (com Shift, 10 s).
 */
export function PlaylistTimeline({
  items,
  anchors,
  offers,
  shuffle,
  playing,
  onMove,
  onDropOffer,
  onEdit,
}: {
  items: DraftItem[];
  anchors: DraftAnchor[];
  offers: OfferSummary[];
  shuffle: boolean;
  playing: PlayingNow[];
  onMove: (anchor: DraftAnchor, to: TimelineTarget) => void;
  onDropOffer: (offerId: number, to: TimelineTarget) => void;
  onEdit: (anchor: DraftAnchor) => void;
}) {
  const [zoom, setZoom] = useState<number>(defaultZoom);
  const [drag, setDrag] = useState<{
    key: string;
    originX: number;
    originTime: number;
    time: number;
    moved: boolean;
  } | null>(null);
  const [dropAt, setDropAt] = useState<number | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const keepCenter = useRef<number | null>(null);
  const justDragged = useRef(false);
  const scrolledToPlayhead = useRef(false);

  const scale = pxPerMs(zoom);
  const total = totalDuration(items);
  const starts = startTimes(items);
  const x = (ms: number) => pad + ms * scale;
  const itemTitle = (key: string) => items.find((i) => i.key === key)?.title ?? '';

  const blocks = anchors.flatMap((anchor) => {
    const preview = drag?.key === anchor.key ? locate(items, drag.time) : null;
    const shown = preview ? { ...anchor, ...preview } : anchor;
    const start = anchorTime(items, shown);
    if (start === null) return [];
    const duration = offers.find((o) => o.id === anchor.offerId)?.durationMs ?? fallbackOfferMs;
    return [{ anchor, shown, start, end: start + duration }];
  });
  const rows = assignRows(blocks.map((b) => ({ key: b.anchor.key, start: b.start, end: b.end })));
  const rowCount = Math.max(1, ...[...rows.values()].map((r) => r + 1));
  const offersHeight = rowCount * rowHeight + 8;
  const height = rulerHeight + offersHeight + musicHeight + 6;
  const playheads = playing.flatMap((p) => {
    const index = items.findIndex((i) => i.hash === p.hash);
    return index < 0 ? [] : [{ ...p, at: starts[index]! + (p.positionMs ?? 0) }];
  });

  const timeAt = (clientX: number) => {
    const el = scroller.current;
    if (!el) return 0;
    return (clientX - el.getBoundingClientRect().left + el.scrollLeft - pad) / scale;
  };

  const changeZoom = (next: number) => {
    const el = scroller.current;
    if (el) keepCenter.current = (el.scrollLeft + el.clientWidth / 2 - pad) / scale;
    setZoom(next);
  };

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (keepCenter.current !== null) {
      el.scrollLeft = pad + keepCenter.current * scale - el.clientWidth / 2;
      keepCenter.current = null;
    } else if (!scrolledToPlayhead.current && playheads.length > 0) {
      // Abre mostrando o que está tocando.
      scrolledToPlayhead.current = true;
      el.scrollLeft = Math.max(0, x(playheads[0]!.at) - el.clientWidth / 3);
    }
  });

  const zoomIndex = zoomLevels.indexOf(zoom as (typeof zoomLevels)[number]);
  const dropTarget = dropAt === null ? null : locate(items, dropAt);

  return (
    <Card>
      <CardHeader
        title="Linha do tempo"
        subtitle={
          shuffle
            ? 'No aleatório a ordem muda a cada ciclo; a linha mostra só a ordem da lista.'
            : 'Arraste uma oferta da coluna Ofertas para o ponto exato, ou arraste um bloco para mudar. Clique no bloco para ajustar.'
        }
        actions={
          <div className="flex items-center gap-1">
            <IconButton
              label="Diminuir o zoom"
              disabled={zoomIndex <= 0}
              onClick={() => changeZoom(zoomLevels[zoomIndex - 1]!)}
            >
              <ZoomOut className="size-4" />
            </IconButton>
            <IconButton
              label="Aumentar o zoom"
              disabled={zoomIndex >= zoomLevels.length - 1}
              onClick={() => changeZoom(zoomLevels[zoomIndex + 1]!)}
            >
              <ZoomIn className="size-4" />
            </IconButton>
          </div>
        }
      />
      {items.length === 0 ? (
        <p className="px-5 py-4 text-sm text-stone-500">Adicione músicas para ver a linha do tempo.</p>
      ) : (
        <div ref={scroller} className="overflow-x-auto px-0 pb-2" data-testid="timeline">
          <div
            className="relative select-none"
            style={{ width: x(total) + pad, height }}
            onDragOver={(event) => {
              if (shuffle || !event.dataTransfer.types.includes(OfferDrag)) return;
              event.preventDefault();
              event.dataTransfer.dropEffect = 'copy';
              setDropAt(timeAt(event.clientX));
            }}
            onDragLeave={(event) => {
              if (!(event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget))) {
                setDropAt(null);
              }
            }}
            onDrop={(event) => {
              setDropAt(null);
              const offerId = Number(event.dataTransfer.getData(OfferDrag));
              if (shuffle || !offerId) return;
              event.preventDefault();
              const target = locate(items, timeAt(event.clientX));
              if (target) onDropOffer(offerId, target);
            }}
          >
            {/* Régua */}
            {ticks(total, zoom).map((at) => (
              <div key={at} className="absolute top-0 border-l border-stone-200" style={{ left: x(at), height }}>
                <span className="ml-1 text-[10px] text-stone-400 tabular-nums">{formatDuration(at)}</span>
              </div>
            ))}

            {/* Ofertas presas */}
            {shuffle ? (
              <p
                className="absolute flex items-center gap-1.5 text-xs text-stone-500"
                style={{ left: pad, top: rulerHeight + 6 }}
              >
                <Lock className="size-3.5" aria-hidden /> Sem ofertas presas no aleatório.
              </p>
            ) : (
              blocks.map(({ anchor, shown, start, end }) => {
                const top = rulerHeight + (rows.get(anchor.key) ?? 0) * rowHeight + 4;
                const dragging = drag?.key === anchor.key;
                return (
                  <div key={anchor.key}>
                    <div
                      className="pointer-events-none absolute w-px bg-amber-500"
                      style={{ left: x(start), top: top + rowHeight - 6, height: height - top - rowHeight + 2 }}
                      aria-hidden
                    />
                    <button
                      type="button"
                      aria-label={`${anchor.offerName}: entra aos ${formatDuration(shown.offsetMs)} de ${itemTitle(shown.itemKey)}`}
                      title="Arraste para mudar; clique para ajustar"
                      className={`absolute flex touch-none items-center gap-1 overflow-hidden rounded-md bg-amber-100 px-1.5 text-left text-[11px] font-medium whitespace-nowrap text-amber-950 ring-1 ring-amber-300 focus-visible:ring-2 focus-visible:ring-brand-600 focus-visible:outline-none ${
                        dragging
                          ? 'z-20 cursor-grabbing shadow-lg ring-2 ring-amber-500'
                          : 'z-10 cursor-grab hover:bg-amber-200'
                      }`}
                      style={{ left: x(start), top, height: rowHeight - 6, width: Math.max(18, (end - start) * scale) }}
                      onPointerDown={(event) => {
                        if (event.button !== 0) return;
                        event.currentTarget.setPointerCapture?.(event.pointerId);
                        setDrag({
                          key: anchor.key,
                          originX: event.clientX,
                          originTime: start,
                          time: start,
                          moved: false,
                        });
                      }}
                      onPointerMove={(event) => {
                        if (!dragging || !drag) return;
                        const dx = event.clientX - drag.originX;
                        setDrag({ ...drag, time: drag.originTime + dx / scale, moved: drag.moved || Math.abs(dx) > 3 });
                      }}
                      onPointerUp={() => {
                        if (!dragging || !drag) return;
                        const target = locate(items, drag.time);
                        setDrag(null);
                        if (drag.moved && target) {
                          // O clique que vem logo depois do arraste não abre o ajuste.
                          justDragged.current = true;
                          setTimeout(() => (justDragged.current = false), 0);
                          onMove(anchor, target);
                        }
                      }}
                      onPointerCancel={() => setDrag(null)}
                      onClick={() => {
                        if (justDragged.current) {
                          justDragged.current = false;
                          return;
                        }
                        onEdit(anchor);
                      }}
                      onKeyDown={(event) => {
                        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
                        event.preventDefault();
                        const step = (event.shiftKey ? 10_000 : 1000) * (event.key === 'ArrowLeft' ? -1 : 1);
                        const target = locate(items, start + step);
                        if (target) onMove(anchor, target);
                      }}
                    >
                      <Megaphone className="size-3 shrink-0" aria-hidden />
                      <span className="truncate">{anchor.offerName}</span>
                    </button>
                    {dragging && (
                      <span
                        className="pointer-events-none absolute z-30 rounded bg-stone-900 px-1.5 py-0.5 text-[11px] whitespace-nowrap text-white tabular-nums"
                        style={{ left: x(start), top: Math.max(0, top - 22) }}
                      >
                        {itemTitle(shown.itemKey)} · aos {formatDuration(shown.offsetMs)}
                      </span>
                    )}
                  </div>
                );
              })
            )}

            {/* Músicas */}
            {items.map((item, index) => {
              const live = playheads.some(
                (p) => p.hash === item.hash && items.findIndex((i) => i.hash === p.hash) === index,
              );
              return (
                <div
                  key={item.key}
                  title={`${index + 1}. ${item.title} (${formatDuration(item.durationMs)})`}
                  className={`absolute overflow-hidden rounded-md px-2 py-1 text-xs ring-1 ring-inset ${
                    live
                      ? 'bg-brand-50 text-brand-900 ring-brand-300'
                      : index % 2 === 0
                        ? 'bg-stone-100 text-stone-800 ring-stone-200'
                        : 'bg-stone-50 text-stone-800 ring-stone-200'
                  }`}
                  style={{
                    left: x(starts[index]!) + 1,
                    top: rulerHeight + offersHeight,
                    width: Math.max(2, item.durationMs * scale - 2),
                    height: musicHeight,
                  }}
                >
                  <p className="truncate font-semibold">{item.title}</p>
                  <p className="truncate text-[11px] text-stone-500 tabular-nums">
                    {index + 1}ª · {formatDuration(item.durationMs)}
                  </p>
                </div>
              );
            })}

            {/* Onde a oferta vai ser solta */}
            {dropTarget && dropAt !== null && (
              <>
                <div
                  className="pointer-events-none absolute top-0 z-30 border-l-2 border-dashed border-amber-600"
                  style={{ left: x(anchorTime(items, dropTarget) ?? 0), height }}
                />
                <span
                  className="pointer-events-none absolute z-30 rounded bg-amber-700 px-1.5 py-0.5 text-[11px] whitespace-nowrap text-white tabular-nums"
                  style={{ left: x(anchorTime(items, dropTarget) ?? 0) + 4, top: rulerHeight }}
                >
                  {itemTitle(dropTarget.itemKey)} · aos {formatDuration(dropTarget.offsetMs)}
                </span>
              </>
            )}

            {/* Tocando agora */}
            {playheads.map((p) => (
              <div
                key={p.storeName}
                className="pointer-events-none absolute top-0 z-20 w-0.5 bg-brand-600"
                style={{ left: x(p.at), height }}
              >
                <span className="absolute top-0 left-1 inline-flex items-center gap-1 rounded bg-brand-700 px-1.5 py-0.5 text-[10px] font-medium whitespace-nowrap text-white">
                  <Volume2 className="size-3" aria-hidden /> {p.storeName}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}
    </Card>
  );
}
