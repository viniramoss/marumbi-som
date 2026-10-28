import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { CalendarClock, Copy, Plus, Save, Trash2, Undo2 } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { usePlaylists, useSaveSchedules } from '../../api/queries';
import type { PlaylistSummary, StoreDetail } from '../../api/types';
import { useToast } from '../../components/Toasts';
import { Button, Card, CardHeader, EmptyState, ErrorNote, IconButton, Select } from '../../components/ui';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { formatLongDuration, weekdayLetter, weekdayShort } from '../../lib/format';
import {
  crossesMidnight,
  durationMinutes,
  newRow,
  newRowKey,
  rowsEqual,
  rowsFrom,
  toScheduleInput,
  validateRows,
  weekOrder,
  type ScheduleRow,
} from './schedule';
import { WeekGrid } from './WeekGrid';

/** Aba "Agenda" (D23 h): lista de faixas editável e a grade semanal ao lado, que acompanha a edição. */
export function ScheduleTab({ detail }: { detail: StoreDetail }) {
  const storeId = detail.status.id;
  const saved = useMemo(() => rowsFrom(detail.schedules), [detail.schedules]);
  const [rows, setRows] = useState(saved);
  const [base, setBase] = useState(saved);
  const [selected, setSelected] = useState<number | null>(null);
  const playlists = usePlaylists();
  const save = useSaveSchedules(storeId);
  const toast = useToast();

  // Agenda recarregada do Hub (outra aba ou outro administrador): acompanha se não houver edição em andamento.
  if (base !== saved) {
    setBase(saved);
    if (rowsEqual(rows, base)) setRows(saved);
  }

  const dirty = !rowsEqual(rows, saved);
  const problems = useMemo(() => validateRows(rows), [rows]);
  const badRows = new Set(problems.flatMap((p) => p.rows));
  const leaveDialog = useUnsavedChanges(
    dirty,
    'A agenda foi alterada e ainda não foi salva. A loja continua com a agenda anterior.',
  );

  const update = (index: number, change: Partial<ScheduleRow>) =>
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...change } : row)));

  const submit = async () => {
    try {
      await save.mutateAsync(toScheduleInput(rows));
      toast.success(
        detail.status.online
          ? 'Agenda salva. A loja aplica em instantes.'
          : 'Agenda salva. A loja aplica quando se conectar.',
      );
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  const playlistList = playlists.data ?? [];

  return (
    <>
      <div className="grid items-start gap-6 2xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <Card>
          <CardHeader
            title="Faixas de horário"
            subtitle={`Horários no fuso da loja (${detail.timezone}). Fora das faixas a loja fica em silêncio.`}
            actions={
              <>
                {dirty && (
                  <Button variant="ghost" icon={<Undo2 className="size-4" />} onClick={() => setRows(saved)}>
                    Desfazer
                  </Button>
                )}
                <Button
                  variant="primary"
                  icon={<Save className="size-4" />}
                  busy={save.isPending}
                  disabled={!dirty || problems.length > 0}
                  onClick={() => void submit()}
                >
                  Salvar agenda
                </Button>
              </>
            }
          />
          <div className="space-y-3 px-5 py-4">
            {playlists.isSuccess && playlistList.length === 0 && (
              <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 ring-1 ring-amber-200">
                Ainda não há playlists.{' '}
                <Link to="/playlists" className="font-medium underline">
                  Crie uma playlist
                </Link>{' '}
                antes de montar a agenda.
              </p>
            )}
            {rows.length === 0 ? (
              <EmptyState icon={<CalendarClock className="size-8" />} title="Agenda vazia">
                Sem faixas de horário a loja não toca nada. Adicione a primeira faixa.
              </EmptyState>
            ) : (
              <ol className="space-y-3" aria-label="Faixas da agenda">
                {rows.map((row, index) => (
                  <RowEditor
                    key={row.key}
                    row={row}
                    index={index}
                    playlists={playlistList}
                    invalid={badRows.has(index)}
                    highlighted={selected === index}
                    onFocus={() => setSelected(index)}
                    onChange={(change) => update(index, change)}
                    onDuplicate={() =>
                      setRows((current) => [
                        ...current.slice(0, index + 1),
                        { ...row, key: newRowKey() },
                        ...current.slice(index + 1),
                      ])
                    }
                    onRemove={() => {
                      setRows((current) => current.filter((_, i) => i !== index));
                      setSelected(null);
                    }}
                  />
                ))}
              </ol>
            )}
            {problems.length > 0 && (
              <div className="space-y-1.5">
                {problems.map((problem) => (
                  <ErrorNote key={problem.message}>{problem.message}</ErrorNote>
                ))}
              </div>
            )}
            <Button
              icon={<Plus className="size-4" />}
              onClick={() => setRows((current) => [...current, newRow(current.at(-1))])}
            >
              Adicionar faixa
            </Button>
          </div>
        </Card>
        <Card>
          <CardHeader
            title="Semana"
            subtitle={dirty ? 'Mostrando a agenda em edição, ainda não salva.' : 'Agenda salva.'}
          />
          <div className="px-3 py-4 sm:px-5">
            <WeekGrid
              rows={rows}
              playlists={playlistList}
              invalid={badRows}
              selected={selected}
              onSelect={setSelected}
            />
          </div>
        </Card>
      </div>
      {leaveDialog}
    </>
  );
}

function RowEditor({
  row,
  index,
  playlists,
  invalid,
  highlighted,
  onFocus,
  onChange,
  onDuplicate,
  onRemove,
}: {
  row: ScheduleRow;
  index: number;
  playlists: PlaylistSummary[];
  invalid: boolean;
  highlighted: boolean;
  onFocus: () => void;
  onChange: (change: Partial<ScheduleRow>) => void;
  onDuplicate: () => void;
  onRemove: () => void;
}) {
  const toggleDay = (day: number) =>
    onChange({
      days: row.days.includes(day) ? row.days.filter((d) => d !== day) : [...row.days, day].sort((a, b) => a - b),
    });
  const unknownPlaylist =
    row.playlistId !== null && playlists.length > 0 && !playlists.some((p) => p.id === row.playlistId);
  const timesValid = /^\d\d:\d\d$/.test(row.start) && /^\d\d:\d\d$/.test(row.end);

  return (
    <li
      onFocusCapture={onFocus}
      onMouseDown={onFocus}
      className={`rounded-lg p-3 ring-1 transition-shadow ${
        invalid ? 'bg-red-50/40 ring-red-300' : highlighted ? 'ring-2 ring-brand-500' : 'ring-stone-200'
      }`}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <span className="w-6 text-sm font-semibold text-stone-400 tabular-nums">{index + 1}</span>
        <Select
          aria-label={`Playlist da faixa ${index + 1}`}
          value={row.playlistId ?? ''}
          onChange={(e) => onChange({ playlistId: e.target.value === '' ? null : Number(e.target.value) })}
          className="w-full min-w-44 flex-1 sm:w-auto"
        >
          <option value="">Escolha a playlist…</option>
          {unknownPlaylist && <option value={row.playlistId!}>Playlist {row.playlistId} (removida)</option>}
          {playlists.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
        <div className="flex items-center gap-2 text-sm">
          <input
            type="time"
            step={60}
            aria-label={`Início da faixa ${index + 1}`}
            value={row.start}
            onChange={(e) => onChange({ start: e.target.value })}
            className="rounded-lg border-0 px-2 py-1.5 text-sm shadow-sm ring-1 ring-stone-300 focus:ring-2 focus:ring-brand-600 focus:outline-none"
          />
          <span className="text-stone-500">até</span>
          <input
            type="time"
            step={60}
            aria-label={`Fim da faixa ${index + 1}`}
            value={row.end}
            onChange={(e) => onChange({ end: e.target.value })}
            className="rounded-lg border-0 px-2 py-1.5 text-sm shadow-sm ring-1 ring-stone-300 focus:ring-2 focus:ring-brand-600 focus:outline-none"
          />
        </div>
        <div className="ml-auto flex">
          <IconButton label={`Duplicar faixa ${index + 1}`} onClick={onDuplicate}>
            <Copy className="size-4" />
          </IconButton>
          <IconButton label={`Remover faixa ${index + 1}`} onClick={onRemove}>
            <Trash2 className="size-4" />
          </IconButton>
        </div>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-2 pl-9">
        <div className="flex gap-1" role="group" aria-label={`Dias da faixa ${index + 1}`}>
          {weekOrder.map((day) => {
            const on = row.days.includes(day);
            return (
              <button
                key={day}
                type="button"
                aria-pressed={on}
                aria-label={weekdayShort[day]}
                title={weekdayShort[day]}
                onClick={() => toggleDay(day)}
                className={`size-8 rounded-full text-xs font-semibold transition-colors ${
                  on ? 'bg-brand-700 text-white hover:bg-brand-800' : 'bg-stone-100 text-stone-500 hover:bg-stone-200'
                }`}
              >
                {weekdayLetter[day]}
              </button>
            );
          })}
        </div>
        {timesValid && (
          <span className="text-xs text-stone-500">
            {formatLongDuration(durationMinutes(row) * 60_000)}
            {row.end === '00:00' && row.start !== '00:00' && ' · até a meia-noite'}
            {row.start === row.end && row.end !== '00:00' && ' · 24 horas'}
            {crossesMidnight(row) && row.start !== row.end && ` · termina no dia seguinte às ${row.end}`}
          </span>
        )}
      </div>
    </li>
  );
}
