import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react';
import { clamp, formatPrecise, rulerStep, type Cut } from './cuts';
import { BucketMs, type Waveform } from './useWaveform';

type Drag =
  | { kind: 'new'; anchor: number; current: number; startX: number }
  | { kind: 'resize'; index: number; edge: 'start' | 'end'; current: number };

const Height = 160;

/**
 * Onda da música com os trechos marcados (D34). Arrastar na onda marca um trecho; arrastar a borda de um trecho
 * ajusta; um clique leva a agulha para o ponto (e seleciona o trecho, se cair num).
 */
export function WaveformView({
  waveform,
  cuts,
  pending,
  positionMs,
  zoom,
  selected,
  onSeek,
  onChange,
  onSelect,
}: {
  waveform: Waveform;
  cuts: readonly Cut[];
  /** Início marcado com "Marcar início", esperando o fim. */
  pending: number | null;
  positionMs: number;
  zoom: number;
  selected: number | null;
  onSeek: (ms: number) => void;
  onChange: (cuts: Cut[]) => void;
  onSelect: (index: number | null) => void;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const track = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [width, setWidth] = useState(0);
  const [drag, setDrag] = useState<Drag | null>(null);
  const duration = waveform.durationMs;
  const percent = (ms: number) => `${(clamp(ms, 0, duration) / duration) * 100}%`;

  useLayoutEffect(() => {
    const element = track.current;
    if (!element) return;
    setWidth(element.clientWidth);
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => setWidth(element.clientWidth));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext('2d');
    if (!element || !context || width === 0) return;
    const ratio = window.devicePixelRatio || 1;
    element.width = Math.round(width * ratio);
    element.height = Math.round(Height * ratio);
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, Height);
    context.fillStyle = getComputedStyle(element).color;
    const { peaks } = waveform;
    const perPixel = peaks.length / width;
    const middle = Height / 2;
    for (let x = 0; x < width; x++) {
      let peak = 0;
      const end = Math.min(peaks.length, Math.ceil((x + 1) * perPixel));
      for (let i = Math.floor(x * perPixel); i < end; i++) peak = Math.max(peak, peaks[i] ?? 0);
      const half = Math.max(0.5, peak * (middle - 4));
      context.fillRect(x, middle - half, 1, half * 2);
    }
  }, [waveform, width]);

  // Com zoom, a agulha não sai da tela enquanto toca.
  useEffect(() => {
    const element = scroller.current;
    if (!element || zoom === 1 || width === 0) return;
    const x = (positionMs / duration) * width;
    if (x < element.scrollLeft || x > element.scrollLeft + element.clientWidth - 24) {
      element.scrollLeft = Math.max(0, x - element.clientWidth / 4);
    }
  }, [positionMs, duration, width, zoom]);

  const msAt = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect || rect.width === 0) return 0;
    return Math.round(clamp((clientX - rect.left) / rect.width, 0, 1) * duration);
  };

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    const target = event.target as HTMLElement;
    const ms = msAt(event.clientX);
    const index = Number(target.dataset.index);
    if (target.dataset.edge && Number.isInteger(index)) {
      setDrag({ kind: 'resize', index, edge: target.dataset.edge === 'start' ? 'start' : 'end', current: ms });
      onSelect(index);
    } else {
      setDrag({ kind: 'new', anchor: ms, current: ms, startX: event.clientX });
    }
    track.current?.setPointerCapture?.(event.pointerId);
  };

  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (drag) setDrag({ ...drag, current: msAt(event.clientX) });
  };

  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    if (!drag) return;
    const ms = msAt(event.clientX);
    setDrag(null);
    if (drag.kind === 'resize') {
      onChange(
        cuts.map((c, i) => (i === drag.index ? { ...c, [drag.edge === 'start' ? 'startMs' : 'endMs']: ms } : c)),
      );
    } else if (Math.abs(event.clientX - drag.startX) < 4) {
      const inside = cuts.findIndex((c) => ms >= c.startMs && ms < c.endMs);
      onSelect(inside >= 0 ? inside : null);
      onSeek(ms);
    } else {
      onChange([...cuts, { startMs: Math.min(drag.anchor, ms), endMs: Math.max(drag.anchor, ms) }]);
    }
  };

  // Durante o arraste, mostra o trecho como vai ficar.
  const shown = cuts.map((c, i) =>
    drag?.kind === 'resize' && drag.index === i
      ? drag.edge === 'start'
        ? { startMs: Math.min(drag.current, c.endMs), endMs: Math.max(drag.current, c.endMs) }
        : { startMs: Math.min(c.startMs, drag.current), endMs: Math.max(c.startMs, drag.current) }
      : c,
  );
  const draft =
    drag?.kind === 'new' && Math.abs(drag.current - drag.anchor) > BucketMs
      ? { startMs: Math.min(drag.anchor, drag.current), endMs: Math.max(drag.anchor, drag.current) }
      : null;
  const step = rulerStep(width / (duration / 1000) || 1);
  const marks = Array.from({ length: Math.floor(duration / step) + 1 }, (_, i) => i * step);

  return (
    <div ref={scroller} className="overflow-x-auto rounded-lg bg-stone-50 ring-1 ring-stone-200">
      <div style={{ width: `${zoom * 100}%` }} className="min-w-full">
        <div
          ref={track}
          data-testid="waveform"
          className="relative cursor-crosshair touch-none text-stone-500 select-none"
          style={{ height: Height }}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={() => setDrag(null)}
        >
          <canvas ref={canvas} className="absolute inset-0 h-full w-full" aria-hidden />
          {shown.map((cut, index) => (
            <div
              key={index}
              role="img"
              aria-label={`Trecho ${index + 1}: de ${formatPrecise(cut.startMs)} a ${formatPrecise(cut.endMs)}`}
              className={`absolute inset-y-0 border-x-2 border-red-600 ${
                selected === index ? 'bg-red-600/30' : 'bg-red-500/20'
              } bg-[repeating-linear-gradient(135deg,transparent_0_6px,rgb(220_38_38/0.18)_6px_9px)]`}
              style={{ left: percent(cut.startMs), width: percent(cut.endMs - cut.startMs) }}
            >
              <span className="pointer-events-none absolute top-1 left-1 rounded bg-red-700 px-1.5 py-0.5 text-[11px] font-medium text-white">
                {index + 1} · tirar
              </span>
              <span
                data-edge="start"
                data-index={index}
                className="absolute inset-y-0 -left-1.5 w-3 cursor-ew-resize"
                title="Arraste para ajustar o início"
              />
              <span
                data-edge="end"
                data-index={index}
                className="absolute inset-y-0 -right-1.5 w-3 cursor-ew-resize"
                title="Arraste para ajustar o fim"
              />
            </div>
          ))}
          {draft && (
            <div
              className="pointer-events-none absolute inset-y-0 border-x-2 border-amber-500 bg-amber-400/25"
              style={{ left: percent(draft.startMs), width: percent(draft.endMs - draft.startMs) }}
            />
          )}
          {pending !== null && (
            <div
              className="pointer-events-none absolute inset-y-0 border-l-2 border-dashed border-amber-600"
              style={{ left: percent(pending) }}
            >
              <span className="absolute bottom-1 left-1 rounded bg-amber-600 px-1.5 py-0.5 text-[11px] font-medium text-white">
                início
              </span>
            </div>
          )}
          <div
            className="pointer-events-none absolute inset-y-0 w-0.5 -translate-x-1/2 bg-brand-700"
            style={{ left: percent(positionMs) }}
            data-testid="playhead"
          />
        </div>
        <div className="relative h-6 border-t border-stone-200 text-[11px] text-stone-500 tabular-nums">
          {marks.map((ms) => (
            <span
              key={ms}
              className="absolute top-0 h-full border-l border-stone-300 pl-1"
              style={{ left: percent(ms) }}
            >
              {formatPrecise(ms).replace(/,0$/, '')}
            </span>
          ))}
        </div>
      </div>
    </div>
  );
}
