import { useMemo, useState } from 'react';
import { Search } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useTracks } from '../../api/queries';
import type { Track } from '../../api/types';
import { Modal } from '../../components/Modal';
import { Button, EmptyState, ErrorNote, Input, Spinner } from '../../components/ui';
import { formatDuration, formatLongDuration, matchesSearch } from '../../lib/format';

type Picked = Pick<Track, 'hash' | 'title' | 'artist' | 'durationMs'>;

/** Escolhe músicas da biblioteca para o fim da playlist, na ordem em que foram marcadas. */
export function AddTracksDialog({
  existing,
  onClose,
  onAdd,
}: {
  existing: Set<string>;
  onClose: () => void;
  onAdd: (tracks: Picked[]) => void;
}) {
  const tracks = useTracks();
  const [query, setQuery] = useState('');
  const [picked, setPicked] = useState<string[]>([]);

  const list = useMemo(
    () => (tracks.data ?? []).filter((t) => matchesSearch(query, t.title, t.artist, t.originalName)),
    [tracks.data, query],
  );
  const byHash = useMemo(() => new Map((tracks.data ?? []).map((t) => [t.hash, t])), [tracks.data]);
  const pickedTracks = picked.map((hash) => byHash.get(hash)).filter((t): t is Track => t !== undefined);
  const pickedMs = pickedTracks.reduce((sum, t) => sum + t.durationMs, 0);

  const toggle = (hash: string) =>
    setPicked((current) => (current.includes(hash) ? current.filter((h) => h !== hash) : [...current, hash]));

  const allVisiblePicked = list.length > 0 && list.every((t) => picked.includes(t.hash));
  const toggleVisible = () =>
    setPicked((current) =>
      allVisiblePicked
        ? current.filter((hash) => !list.some((t) => t.hash === hash))
        : [...current, ...list.map((t) => t.hash).filter((hash) => !current.includes(hash))],
    );

  return (
    <Modal
      title="Adicionar músicas"
      wide
      onClose={onClose}
      footer={
        <>
          <span className="mr-auto self-center text-sm text-stone-500">
            {picked.length === 0
              ? 'Nenhuma marcada'
              : `${picked.length} ${picked.length === 1 ? 'marcada' : 'marcadas'} · ${formatLongDuration(pickedMs)}`}
          </span>
          <Button onClick={onClose}>Cancelar</Button>
          <Button
            variant="primary"
            disabled={pickedTracks.length === 0}
            onClick={() =>
              onAdd(pickedTracks.map(({ hash, title, artist, durationMs }) => ({ hash, title, artist, durationMs })))
            }
          >
            {pickedTracks.length > 0 ? `Adicionar ${pickedTracks.length}` : 'Adicionar'}
          </Button>
        </>
      }
    >
      <div className="relative mb-3">
        <Search
          className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-stone-400"
          aria-hidden
        />
        <Input
          type="search"
          placeholder="Buscar por título, artista ou arquivo"
          aria-label="Buscar na biblioteca"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="pl-9"
        />
      </div>
      {tracks.isPending ? (
        <Spinner />
      ) : tracks.isError ? (
        <ErrorNote>{errorMessage(tracks.error)}</ErrorNote>
      ) : list.length === 0 ? (
        <EmptyState title={query ? 'Nenhuma música encontrada' : 'A biblioteca está vazia'}>
          {!query && 'Envie músicas na Biblioteca primeiro.'}
        </EmptyState>
      ) : (
        <>
          <label className="mb-1 flex items-center gap-3 px-2 py-1.5 text-xs font-medium text-stone-500">
            <input
              type="checkbox"
              checked={allVisiblePicked}
              onChange={toggleVisible}
              className="size-4 accent-brand-700"
            />
            {allVisiblePicked ? 'Desmarcar' : 'Marcar'} {query ? 'as encontradas' : 'todas'} ({list.length})
          </label>
          <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200">
            {list.map((track) => {
              const order = picked.indexOf(track.hash);
              return (
                <li key={track.hash}>
                  <label className="flex cursor-pointer items-center gap-3 px-2 py-2 text-sm hover:bg-stone-50">
                    <input
                      type="checkbox"
                      checked={order >= 0}
                      onChange={() => toggle(track.hash)}
                      className="size-4 accent-brand-700"
                      aria-label={`Marcar ${track.title}`}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium text-stone-900">{track.title}</span>
                      <span className="block truncate text-xs text-stone-500">
                        {track.artist ?? 'Artista desconhecido'}
                      </span>
                    </span>
                    {existing.has(track.hash) && (
                      <span className="shrink-0 rounded bg-stone-100 px-1.5 py-0.5 text-[11px] text-stone-600">
                        já está na playlist
                      </span>
                    )}
                    {order >= 0 && (
                      <span className="w-6 shrink-0 text-center text-xs font-semibold text-brand-700 tabular-nums">
                        {order + 1}º
                      </span>
                    )}
                    <span className="w-12 shrink-0 text-right text-stone-600 tabular-nums">
                      {formatDuration(track.durationMs)}
                    </span>
                  </label>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </Modal>
  );
}
