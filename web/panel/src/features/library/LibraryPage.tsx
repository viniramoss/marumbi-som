import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { Check, Library, Pause, Pencil, Play, Scissors, Search, Trash2, X } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useDeleteTrack, useTracks, useUpdateTrack } from '../../api/queries';
import type { Track } from '../../api/types';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Badge, Card, EmptyState, ErrorNote, IconButton, Input, PageHeader, Spinner } from '../../components/ui';
import { usePreview } from '../../hooks/usePreview';
import { formatDateTime, formatDuration, formatLongDuration, matchesSearch } from '../../lib/format';
import { TrackSubtitle } from './TrackSubtitle';
import { UploadPanel } from './UploadPanel';

export function LibraryPage() {
  const tracks = useTracks();
  const [query, setQuery] = useState('');
  const [removing, setRemoving] = useState<Track | null>(null);
  const preview = usePreview();
  const remove = useDeleteTrack();
  const toast = useToast();

  const list = useMemo(
    () => (tracks.data ?? []).filter((t) => matchesSearch(query, t.title, t.artist, t.originalName)),
    [tracks.data, query],
  );
  const totalMs = (tracks.data ?? []).reduce((sum, t) => sum + t.durationMs, 0);

  const confirmRemove = async () => {
    if (!removing) return;
    try {
      await remove.mutateAsync(removing.hash);
      toast.success(`"${removing.title}" saiu da biblioteca.`);
      setRemoving(null);
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  return (
    <>
      <PageHeader
        title="Biblioteca"
        description={
          tracks.data
            ? `${tracks.data.length} ${tracks.data.length === 1 ? 'música' : 'músicas'} · ${formatLongDuration(totalMs)} no total`
            : undefined
        }
      />
      <div className="space-y-6">
        <UploadPanel />

        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-stone-100 px-5 py-3.5">
            <h2 className="text-base font-semibold">Músicas</h2>
            <div className="relative w-full sm:w-80">
              <Search
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-stone-400"
                aria-hidden
              />
              <Input
                type="search"
                placeholder="Buscar por título, artista ou arquivo"
                aria-label="Buscar músicas"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="pl-9"
              />
            </div>
          </div>
          {tracks.isPending ? (
            <Spinner />
          ) : tracks.isError ? (
            <div className="p-5">
              <ErrorNote>{errorMessage(tracks.error)}</ErrorNote>
            </div>
          ) : list.length === 0 ? (
            <EmptyState
              icon={<Library className="size-10" />}
              title={query ? 'Nenhuma música encontrada' : 'A biblioteca está vazia'}
            >
              {!query && 'Envie as primeiras músicas no quadro acima.'}
            </EmptyState>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[820px] text-sm">
                <thead>
                  <tr className="border-b border-stone-100 text-left text-xs font-medium tracking-wide text-stone-500 uppercase">
                    <th className="w-12 px-5 py-2.5" />
                    <th className="w-14 py-2.5 pr-3 text-right">Nº</th>
                    <th className="py-2.5 pr-4">Música</th>
                    <th className="w-20 py-2.5 pr-4 text-right">Duração</th>
                    <th className="w-36 py-2.5 pr-4" title="Volume do arquivo enviado, antes de igualar em −16 LUFS">
                      Volume original
                    </th>
                    <th className="py-2.5 pr-4">Playlists</th>
                    <th className="w-32 py-2.5 pr-4">Enviada em</th>
                    <th className="w-32 py-2.5 pr-5" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-stone-100">
                  {list.map((track) => (
                    <TrackRow
                      key={track.hash}
                      track={track}
                      playing={preview.playing === track.hash}
                      onPreview={() => preview.toggle(track.hash)}
                      onRemove={() => setRemoving(track)}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      </div>

      {removing && (
        <ConfirmDialog
          title="Remover da biblioteca?"
          confirmLabel="Remover"
          busy={remove.isPending}
          onCancel={() => setRemoving(null)}
          onConfirm={() => void confirmRemove()}
        >
          <p>
            <strong>{removing.title}</strong>
            {removing.artist && ` — ${removing.artist}`} sai da biblioteca.
          </p>
          {removing.playlists.length > 0 ? (
            <p className="rounded-lg bg-amber-50 px-3 py-2 text-amber-900 ring-1 ring-amber-200">
              Ela está nas playlists {removing.playlists.map((p) => p.name).join(', ')}. Tire de lá antes de remover.
            </p>
          ) : (
            <p>Se ela for enviada de novo, volta como estava.</p>
          )}
        </ConfirmDialog>
      )}
    </>
  );
}

function TrackRow({
  track,
  playing,
  onPreview,
  onRemove,
}: {
  track: Track;
  playing: boolean;
  onPreview: () => void;
  onRemove: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(track.title);
  const [artist, setArtist] = useState(track.artist ?? '');
  const update = useUpdateTrack();
  const toast = useToast();
  const navigate = useNavigate();

  const save = async () => {
    try {
      await update.mutateAsync({ hash: track.hash, title: title.trim(), artist: artist.trim() });
      setEditing(false);
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  return (
    <tr className={playing ? 'bg-brand-50/50' : 'hover:bg-stone-50'}>
      <td className="px-5 py-2">
        <IconButton label={playing ? 'Parar pré-escuta' : 'Ouvir'} onClick={onPreview}>
          {playing ? <Pause className="size-4 text-brand-700" /> : <Play className="size-4" />}
        </IconButton>
      </td>
      <td className="py-2 pr-3 text-right text-stone-500 tabular-nums">{track.number ?? '–'}</td>
      <td className="py-2 pr-4">
        {editing ? (
          <form
            title={track.originalName}
            className="flex flex-wrap items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              void save();
            }}
          >
            <Input
              aria-label="Título"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              className="w-56"
              required
              autoFocus
            />
            <Input
              aria-label="Artista"
              value={artist}
              onChange={(e) => setArtist(e.target.value)}
              className="w-44"
              placeholder="Artista"
            />
            <IconButton label="Salvar" type="submit" disabled={update.isPending}>
              <Check className="size-4 text-emerald-700" />
            </IconButton>
            <IconButton
              label="Cancelar"
              onClick={() => {
                setEditing(false);
                setTitle(track.title);
                setArtist(track.artist ?? '');
              }}
            >
              <X className="size-4" />
            </IconButton>
            <span className="w-full truncate text-xs text-stone-400">{track.originalName}</span>
          </form>
        ) : (
          <div className="min-w-0">
            <p className="flex items-center gap-2 font-medium text-stone-900">
              <span className="truncate">{track.title}</span>
              {track.edited && <Badge tone="blue">editada</Badge>}
            </p>
            <TrackSubtitle track={track} />
          </div>
        )}
      </td>
      <td className="py-2 pr-4 text-right text-stone-600 tabular-nums">{formatDuration(track.durationMs)}</td>
      <td className="py-2 pr-4 text-stone-600 tabular-nums">
        {track.loudnessLufs === null
          ? '–'
          : `${track.loudnessLufs.toLocaleString('pt-BR', { maximumFractionDigits: 1 })} LUFS`}
      </td>
      <td className="py-2 pr-4">
        <div className="flex flex-wrap gap-1">
          {track.playlists.length === 0 ? (
            <span className="text-stone-400">Nenhuma</span>
          ) : (
            track.playlists.map((p) => (
              <Link
                key={p.id}
                to={`/playlists/${p.id}`}
                className="rounded-full bg-stone-100 px-2 py-0.5 text-xs text-stone-700 hover:bg-stone-200"
              >
                {p.name}
              </Link>
            ))
          )}
        </div>
      </td>
      <td className="py-2 pr-4 text-stone-500">{formatDateTime(track.createdAt)}</td>
      <td className="py-2 pr-5">
        {!editing && (
          <div className="flex justify-end">
            <IconButton label="Renomear" onClick={() => setEditing(true)}>
              <Pencil className="size-4" />
            </IconButton>
            <IconButton label="Editar trechos" onClick={() => void navigate(`/biblioteca/${track.hash}`)}>
              <Scissors className="size-4" />
            </IconButton>
            <IconButton label="Remover da biblioteca" onClick={onRemove}>
              <Trash2 className="size-4" />
            </IconButton>
          </div>
        )}
      </td>
    </tr>
  );
}
