import type { PlaylistSummary } from '../../api/types';
import { useNow } from '../../hooks/useNow';
import { weekdayShort } from '../../lib/format';
import { gridBlocks, gridHours, weekOrder, type ScheduleRow } from './schedule';

const HourHeight = 26;

// Uma cor por playlist (sempre a mesma para a mesma playlist).
const colors = [
  'bg-orange-100 text-orange-900 ring-orange-300',
  'bg-sky-100 text-sky-900 ring-sky-300',
  'bg-emerald-100 text-emerald-900 ring-emerald-300',
  'bg-violet-100 text-violet-900 ring-violet-300',
  'bg-amber-100 text-amber-900 ring-amber-300',
  'bg-teal-100 text-teal-900 ring-teal-300',
  'bg-fuchsia-100 text-fuchsia-900 ring-fuchsia-300',
  'bg-lime-100 text-lime-900 ring-lime-300',
] as const;

const colorFor = (playlistId: number | null) =>
  playlistId === null ? 'bg-stone-100 text-stone-600 ring-stone-300' : colors[playlistId % colors.length];

/** Grade semanal (seção 7.2): colunas de segunda a domingo, faixas como blocos; a faixa da madrugada aparece nos dois dias. */
export function WeekGrid({
  rows,
  playlists,
  invalid,
  selected,
  onSelect,
}: {
  rows: readonly ScheduleRow[];
  playlists: readonly PlaylistSummary[];
  invalid: ReadonlySet<number>;
  selected: number | null;
  onSelect: (index: number) => void;
}) {
  const now = new Date(useNow(60_000));
  const blocks = gridBlocks(rows);
  const hours = gridHours(blocks);
  const height = (hours.to - hours.from) * HourHeight;
  const names = new Map(playlists.map((p) => [p.id, p.name]));
  const nowMinutes = now.getHours() * 60 + now.getMinutes();
  const nowTop = (nowMinutes / 60 - hours.from) * HourHeight;
  const hourMarks = Array.from({ length: hours.to - hours.from + 1 }, (_, i) => hours.from + i);

  return (
    <div className="overflow-x-auto">
      <div className="min-w-[480px]">
        <div className="grid grid-cols-[40px_repeat(7,minmax(0,1fr))] gap-x-1 pb-1">
          <span />
          {weekOrder.map((day) => (
            <span
              key={day}
              className={`text-center text-xs font-semibold ${day === now.getDay() ? 'text-brand-800' : 'text-stone-500'}`}
            >
              {weekdayShort[day]}
            </span>
          ))}
        </div>
        <div className="grid grid-cols-[40px_repeat(7,minmax(0,1fr))] gap-x-1">
          <div className="relative" style={{ height }}>
            {hourMarks.map((hour) => (
              <span
                key={hour}
                className="absolute right-1 -translate-y-1/2 text-[10px] text-stone-400 tabular-nums"
                style={{ top: (hour - hours.from) * HourHeight }}
              >
                {String(hour).padStart(2, '0')}h
              </span>
            ))}
          </div>
          {weekOrder.map((day) => (
            <div
              key={day}
              className="relative rounded-md bg-stone-50"
              style={{
                height,
                backgroundImage: `repeating-linear-gradient(to bottom, transparent 0, transparent ${HourHeight - 1}px, rgb(231 229 228) ${HourHeight - 1}px, rgb(231 229 228) ${HourHeight}px)`,
              }}
            >
              {blocks
                .filter((block) => block.day === day)
                .map((block) => {
                  const row = rows[block.index]!;
                  const top = (block.from / 60 - hours.from) * HourHeight;
                  const blockHeight = ((block.to - block.from) / 60) * HourHeight;
                  const name =
                    row.playlistId === null
                      ? 'Sem playlist'
                      : (names.get(row.playlistId) ?? `Playlist ${row.playlistId}`);
                  const bad = invalid.has(block.index);
                  return (
                    <button
                      key={`${block.index}-${block.day}-${block.from}`}
                      type="button"
                      onClick={() => onSelect(block.index)}
                      title={`Faixa ${block.index + 1}: ${name}, ${row.start}–${row.end === '00:00' ? '24:00' : row.end}`}
                      className={`absolute inset-x-0.5 overflow-hidden rounded px-1 py-0.5 text-left text-[11px] leading-tight ring-1 ${
                        bad ? 'bg-red-100 text-red-900 ring-red-400' : colorFor(row.playlistId)
                      } ${selected === block.index ? 'z-10 ring-2' : ''} ${block.continued ? 'rounded-t-none' : ''}`}
                      style={{ top, height: Math.max(blockHeight, 6) }}
                    >
                      {blockHeight >= 18 && <span className="block truncate font-semibold">{name}</span>}
                      {blockHeight >= 32 && (
                        <span className="block truncate opacity-75">
                          {block.continued
                            ? `até ${row.end}`
                            : `${row.start}–${row.end === '00:00' ? '24:00' : row.end}`}
                        </span>
                      )}
                    </button>
                  );
                })}
              {day === now.getDay() && nowTop >= 0 && nowTop <= height && (
                <div
                  className="pointer-events-none absolute inset-x-0 z-20 border-t-2 border-red-500"
                  style={{ top: nowTop }}
                  aria-hidden
                />
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
