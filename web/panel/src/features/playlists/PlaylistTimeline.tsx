import { useLayoutEffect, useRef, useState } from 'react';
import { Lock, Maximize2, Megaphone, Music, Volume2, ZoomIn, ZoomOut } from 'lucide-react';
import type { OfferSummary } from '../../api/types';
import { Card, CardHeader, IconButton } from '../../components/ui';
import { formatDuration, formatLongDuration } from '../../lib/format';
import { startTimes, totalDuration, type DraftAnchor, type DraftItem } from './playlistDraft';
import { anchorTime, assignRows, fitZoom, locate, minorTicks, nextZoom, pxPerMs, snapToStart, ticks } from './timeline';

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

const pad = 14;
const rowHeight = 30;
const rulerHeight = 28;
const musicHeight = 58;
/** Oferta sem duração conhecida (removida do cadastro) aparece com 10 s. */
const fallbackOfferMs = 10_000;
/** Largura aproximada do rótulo da oferta (ícone, nome e horário), para os rótulos não se encavalarem. */
const chipWidth = (name: string) => Math.min(240, Math.round(name.length * 6.6) + 68);

/**
 * Linha do tempo da playlist inteira (D30), como num editor de vídeo: as músicas em sequência do começo ao fim e as
 * ofertas presas marcadas no ponto em que entram. Abre mostrando a playlist toda; as lupas (ou Ctrl + rolar o mouse)
 * aproximam para posicionar com precisão. Soltar uma oferta da coluna da direita num ponto a prende ali; arrastar o
 * rótulo muda o ponto; clicar abre o ajuste fino; com o rótulo selecionado, as setas movem 1 s (com Shift, 10 s).
 * Por baixo continua sendo a âncora da D26: a oferta fica presa à música daquele ponto e ao "entra aos".
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
  /** Pixels por minuto; nulo = ajustado para caber a playlist inteira. */
  const [zoom, setZoom] = useState<number | null>(null);
  const [width, setWidth] = useState(0);
  const [drag, setDrag] = useState<{
    key: string;
    originX: number;
    originTime: number;
    time: number;
    moved: boolean;
  } | null>(null);
  const [dropAt, setDropAt] = useState<number | null>(null);
  const [hoverAt, setHoverAt] = useState<number | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const keep = useRef<{ time: number; viewX: number } | null>(null);
  const justDragged = useRef(false);
  const scrolledToPlayhead = useRef(false);
  const onWheel = useRef<(event: WheelEvent) => void>(() => {});

  const total = totalDuration(items);
  const starts = startTimes(items);
  const fit = fitZoom(total, width - 2 * pad);
  const pxPerMinute = zoom ?? fit;
  const scale = pxPerMs(pxPerMinute);
  const x = (ms: number) => pad + ms * scale;
  const contentWidth = Math.max(x(total) + pad, width);
  const itemTitle = (key: string) => items.find((i) => i.key === key)?.title ?? '';
  const describe = (target: TimelineTarget) => {
    const at = formatDuration(anchorTime(items, target));
    return target.offsetMs === 0
      ? `${at} · começo de ${itemTitle(target.itemKey)}`
      : `${at} · ${itemTitle(target.itemKey)} aos ${formatDuration(target.offsetMs)}`;
  };

  const blocks = anchors.flatMap((anchor) => {
    const preview = drag?.key === anchor.key ? locate(items, drag.time) : null;
    const shown = preview ? { ...anchor, ...preview } : anchor;
    const start = anchorTime(items, shown);
    if (start === null) return [];
    const duration = offers.find((o) => o.id === anchor.offerId)?.durationMs ?? fallbackOfferMs;
    // Perto do fim da linha o rótulo fica do lado esquerdo do ponto, para não sair da tela.
    const chip = chipWidth(anchor.offerName);
    const flip = x(start) + chip > contentWidth;
    return [
      {
        anchor,
        shown,
        start,
        duration,
        flip,
        from: flip ? x(start) - chip : x(start),
        to: flip ? x(start) : x(start) + chip,
      },
    ];
  });
  const rows = assignRows(blocks.map((b) => ({ key: b.anchor.key, start: b.from, end: b.to + 6 })));
  const rowCount = Math.max(1, ...[...rows.values()].map((r) => r + 1));
  const offersHeight = rowCount * rowHeight + 10;
  const musicTop = rulerHeight + offersHeight;
  const height = musicTop + musicHeight + 8;
  const playheads = playing.flatMap((p) => {
    const index = items.findIndex((i) => i.hash === p.hash);
    return index < 0 ? [] : [{ ...p, index, at: starts[index]! + (p.positionMs ?? 0) }];
  });
  const majorTicks = ticks(total, pxPerMinute);
  const lastTick = majorTicks[majorTicks.length - 1] ?? 0;

  const timeAt = (clientX: number) => {
    const el = scroller.current;
    if (!el) return 0;
    return (clientX - el.getBoundingClientRect().left + el.scrollLeft - pad) / scale;
  };
  const snapped = (time: number) => snapToStart(items, time, pxPerMinute);

  /** Troca o zoom mantendo parado o ponto que está no meio da tela (ou embaixo do mouse). */
  const zoomTo = (next: number | null, viewX?: number) => {
    const el = scroller.current;
    if (el) {
      const at = viewX ?? el.clientWidth / 2;
      keep.current = { time: (el.scrollLeft + at - pad) / scale, viewX: at };
    }
    setZoom(next);
  };
  const zoomIn = nextZoom(pxPerMinute, 1, fit);
  const zoomOut = zoom === null ? undefined : nextZoom(pxPerMinute, -1, fit);

  const handleWheel = (event: WheelEvent) => {
    if (!event.ctrlKey && !event.metaKey) return;
    event.preventDefault();
    const el = scroller.current;
    const viewX = el ? event.clientX - el.getBoundingClientRect().left : undefined;
    if (event.deltaY < 0 && zoomIn !== null) zoomTo(zoomIn, viewX);
    if (event.deltaY > 0 && zoomOut !== undefined) zoomTo(zoomOut, viewX);
  };
  useLayoutEffect(() => {
    onWheel.current = handleWheel;
  });

  const hasItems = items.length > 0;
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const wheel = (event: WheelEvent) => onWheel.current(event);
    el.addEventListener('wheel', wheel, { passive: false });
    setWidth(el.clientWidth);
    if (typeof ResizeObserver === 'undefined') return () => el.removeEventListener('wheel', wheel);
    const observer = new ResizeObserver(() => setWidth(el.clientWidth));
    observer.observe(el);
    return () => {
      el.removeEventListener('wheel', wheel);
      observer.disconnect();
    };
  }, [hasItems]);

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (keep.current !== null) {
      el.scrollLeft = pad + keep.current.time * scale - keep.current.viewX;
      keep.current = null;
    } else if (!scrolledToPlayhead.current && playheads.length > 0 && width > 0) {
      // Com zoom, abre mostrando o que está tocando.
      scrolledToPlayhead.current = true;
      el.scrollLeft = Math.max(0, x(playheads[0]!.at) - el.clientWidth / 3);
    }
  });

  const dropTarget = dropAt === null ? null : locate(items, dropAt);

  return (
    <Card>
      <CardHeader
        title="Linha do tempo"
        subtitle={
          !hasItems
            ? undefined
            : shuffle
              ? `Playlist inteira · ${formatLongDuration(total)}. No aleatório a ordem muda a cada volta.`
              : `Playlist inteira · ${formatLongDuration(total)}. Arraste uma oferta da coluna Ofertas até o ponto exato.`
        }
        actions={
          hasItems && (
            <div className="flex items-center gap-0.5 rounded-lg p-0.5 ring-1 ring-stone-200">
              <IconButton
                label="Afastar (Ctrl + rolar o mouse)"
                disabled={zoomOut === undefined}
                onClick={() => zoomOut !== undefined && zoomTo(zoomOut)}
                className="size-8"
              >
                <ZoomOut className="size-4" />
              </IconButton>
              <IconButton
                label="Aproximar (Ctrl + rolar o mouse)"
                disabled={zoomIn === null}
                onClick={() => zoomIn !== null && zoomTo(zoomIn)}
                className="size-8"
              >
                <ZoomIn className="size-4" />
              </IconButton>
              <IconButton
                label="Ver a playlist inteira"
                disabled={zoom === null}
                onClick={() => zoomTo(null)}
                className="size-8"
              >
                <Maximize2 className="size-4" />
              </IconButton>
            </div>
          )
        }
      />
      {!hasItems ? (
        <p className="px-5 py-4 text-sm text-stone-500">Adicione músicas para ver a linha do tempo.</p>
      ) : (
        <div className="flex">
          {/* Nomes das faixas, como num editor de vídeo */}
          <div className="w-24 shrink-0 border-r border-stone-200 bg-stone-50 text-xs font-medium text-stone-500">
            <div className="border-b border-stone-200" style={{ height: rulerHeight }} />
            <div className="flex items-center gap-1.5 px-3" style={{ height: offersHeight }}>
              <Megaphone className="size-3.5" aria-hidden /> Ofertas
            </div>
            <div className="flex items-center gap-1.5 px-3" style={{ height: musicHeight + 8 }}>
              <Music className="size-3.5" aria-hidden /> Músicas
            </div>
          </div>

          <div ref={scroller} className="min-w-0 flex-1 overflow-x-auto" data-testid="timeline">
            <div
              className="relative select-none"
              style={{ width: contentWidth, height }}
              onMouseMove={(event) => {
                if (!drag) setHoverAt(timeAt(event.clientX));
              }}
              onMouseLeave={() => setHoverAt(null)}
              onDragOver={(event) => {
                if (shuffle || !event.dataTransfer.types.includes(OfferDrag)) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'copy';
                setDropAt(snapped(timeAt(event.clientX)));
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
                const target = locate(items, snapped(timeAt(event.clientX)));
                if (target) onDropOffer(offerId, target);
              }}
            >
              {/* Régua: tempo desde o começo da playlist */}
              <div
                className="absolute inset-x-0 top-0 border-b border-stone-200 bg-stone-50"
                style={{ height: rulerHeight }}
              />
              {minorTicks(total, pxPerMinute).map((at) => (
                <div
                  key={`m${at}`}
                  className="absolute border-l border-stone-300"
                  style={{ left: x(at), top: rulerHeight - 6, height: 6 }}
                />
              ))}
              {majorTicks.map((at) => (
                <div key={at} className="absolute top-0 border-l border-stone-200" style={{ left: x(at), height }}>
                  <span className="absolute top-1.5 left-1 text-[11px] font-medium text-stone-500 tabular-nums">
                    {formatDuration(at)}
                  </span>
                </div>
              ))}
              <div className="absolute top-0 border-l border-stone-400" style={{ left: x(total), height }}>
                {(total - lastTick) * scale > 44 && (
                  <span className="absolute top-1.5 right-1 text-[11px] font-semibold text-stone-600 tabular-nums">
                    {formatDuration(total)}
                  </span>
                )}
              </div>

              {/* Músicas, uma depois da outra */}
              {items.map((item, index) => {
                const live = playheads.find((p) => p.index === index);
                const blockWidth = Math.max(2, item.durationMs * scale - 2);
                return (
                  <div
                    key={item.key}
                    title={`${index + 1}. ${item.title} (${formatDuration(item.durationMs)})`}
                    className={`absolute overflow-hidden rounded-md ring-1 ring-inset ${
                      live
                        ? 'bg-brand-50 text-brand-900 ring-brand-300'
                        : index % 2 === 0
                          ? 'bg-stone-100 text-stone-800 ring-stone-200'
                          : 'bg-stone-50 text-stone-800 ring-stone-200'
                    }`}
                    style={{ left: x(starts[index]!) + 1, top: musicTop + 4, width: blockWidth, height: musicHeight }}
                  >
                    {live && live.positionMs !== null && (
                      <div
                        className="absolute inset-y-0 left-0 bg-brand-100"
                        style={{ width: Math.min(blockWidth, live.positionMs * scale) }}
                        aria-hidden
                      />
                    )}
                    {blockWidth > 26 && (
                      <div className="relative px-2 py-1.5">
                        <p className="truncate text-xs font-semibold">{item.title}</p>
                        <p className="truncate text-[11px] text-stone-500 tabular-nums">
                          {formatDuration(item.durationMs)}
                        </p>
                      </div>
                    )}
                  </div>
                );
              })}

              {/* Ofertas presas: rótulo no ponto em que entram e faixa por cima da música enquanto tocam */}
              {shuffle ? (
                <p
                  className="absolute flex items-center gap-1.5 text-xs text-stone-500"
                  style={{ left: pad, top: rulerHeight + 8 }}
                >
                  <Lock className="size-3.5" aria-hidden /> Sem ofertas presas no aleatório.
                </p>
              ) : (
                blocks.map(({ anchor, shown, start, duration, flip }) => {
                  const top = rulerHeight + 5 + (rows.get(anchor.key) ?? 0) * rowHeight;
                  const dragging = drag?.key === anchor.key;
                  return (
                    <div key={anchor.key}>
                      <div
                        className={`pointer-events-none absolute ${dragging ? 'z-20 w-0.5 bg-amber-600' : 'z-[5] w-px bg-amber-500'}`}
                        style={{
                          left: x(start),
                          top: top + rowHeight - 6,
                          height: musicTop + 4 - (top + rowHeight - 6),
                        }}
                        aria-hidden
                      />
                      <div
                        className="pointer-events-none absolute z-[6] rounded-b-sm bg-amber-500/85"
                        style={{ left: x(start), top: musicTop + 4, width: Math.max(3, duration * scale), height: 5 }}
                        aria-hidden
                      />
                      <button
                        type="button"
                        aria-label={`${anchor.offerName}: entra aos ${formatDuration(shown.offsetMs)} de ${itemTitle(shown.itemKey)}`}
                        title={`${describe({ itemKey: shown.itemKey, offsetMs: shown.offsetMs ?? 0 })}. Arraste para mudar; clique para ajustar`}
                        className={`absolute flex max-w-[240px] touch-none items-center gap-1 border-amber-600 bg-amber-100 pr-2 pl-1.5 text-[11px] font-medium whitespace-nowrap text-amber-950 ring-1 ring-amber-300 ring-inset focus-visible:ring-2 focus-visible:ring-brand-600 focus-visible:outline-none ${
                          flip ? 'rounded-l-md rounded-r-sm border-r-2' : 'rounded-l-sm rounded-r-md border-l-2'
                        } ${
                          dragging
                            ? 'z-20 cursor-grabbing bg-amber-200 shadow-lg'
                            : 'z-10 cursor-grab shadow-sm hover:bg-amber-200'
                        }`}
                        style={{
                          left: x(start),
                          top,
                          height: rowHeight - 6,
                          transform: flip ? 'translateX(-100%)' : undefined,
                        }}
                        onPointerDown={(event) => {
                          if (event.button !== 0) return;
                          event.currentTarget.setPointerCapture?.(event.pointerId);
                          setHoverAt(null);
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
                          setDrag({
                            ...drag,
                            time: snapped(drag.originTime + dx / scale),
                            moved: drag.moved || Math.abs(dx) > 3,
                          });
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
                        <span className="shrink-0 font-normal text-amber-800/80 tabular-nums">
                          {formatDuration(start)}
                        </span>
                      </button>
                      {dragging && (
                        <span
                          className="pointer-events-none absolute z-30 rounded bg-stone-900 px-1.5 py-0.5 text-[11px] font-medium whitespace-nowrap text-white tabular-nums shadow"
                          style={{ left: x(start) + 4, top: 3 }}
                        >
                          {describe({ itemKey: shown.itemKey, offsetMs: shown.offsetMs ?? 0 })}
                        </span>
                      )}
                    </div>
                  );
                })
              )}

              {/* Guia do mouse */}
              {hoverAt !== null && dropAt === null && !drag && hoverAt >= 0 && hoverAt <= total && (
                <div
                  className="pointer-events-none absolute top-0 z-[4] border-l border-dashed border-stone-400"
                  style={{ left: x(hoverAt), height }}
                >
                  <span className="absolute top-1 left-1 rounded bg-white px-1 text-[11px] font-medium text-stone-700 tabular-nums shadow-sm ring-1 ring-stone-200">
                    {formatDuration(hoverAt)}
                  </span>
                </div>
              )}

              {/* Onde a oferta vai ser solta */}
              {dropTarget && dropAt !== null && (
                <div
                  className="pointer-events-none absolute top-0 z-30 border-l-2 border-dashed border-amber-600"
                  style={{ left: x(anchorTime(items, dropTarget) ?? 0), height }}
                >
                  <span className="absolute top-1 left-1.5 rounded bg-amber-700 px-1.5 py-0.5 text-[11px] font-medium whitespace-nowrap text-white tabular-nums shadow">
                    {describe(dropTarget)}
                  </span>
                </div>
              )}

              {/* Tocando agora */}
              {playheads.map((p) => (
                <div
                  key={p.storeName}
                  className="pointer-events-none absolute top-0 z-[8] w-0.5 bg-brand-600"
                  style={{ left: x(p.at), height }}
                >
                  <span className="absolute top-1 left-1 inline-flex items-center gap-1 rounded bg-brand-700 px-1.5 py-0.5 text-[11px] font-medium whitespace-nowrap text-white shadow">
                    <Volume2 className="size-3" aria-hidden /> {p.storeName}
                  </span>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </Card>
  );
}
