import { Music } from 'lucide-react';
import type { StoreSummary } from '../../api/types';
import { ProgressBar } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { formatDuration } from '../../lib/format';
import { currentPositionMs } from '../../lib/position';

/** Música atual com a barra de progresso andando entre um status e outro. */
export function NowPlaying({ store, large = false }: { store: StoreSummary; large?: boolean }) {
  const now = useNow();
  const track = store.nowPlaying;
  const position = currentPositionMs(track, store.player, now);

  if (!track || !store.online) {
    return (
      <div className="flex items-center gap-3 text-sm text-stone-500">
        <span className="flex size-10 items-center justify-center rounded-lg bg-stone-100">
          <Music className="size-5 text-stone-400" aria-hidden />
        </span>
        {store.online
          ? 'Nada tocando agora.'
          : store.enrolled
            ? 'Sem conexão com a loja.'
            : 'Loja ainda não vinculada.'}
      </div>
    );
  }

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-3">
        <span
          className={`flex shrink-0 items-center justify-center rounded-lg bg-brand-50 ${large ? 'size-14' : 'size-10'}`}
        >
          <Music className={`text-brand-700 ${large ? 'size-7' : 'size-5'}`} aria-hidden />
        </span>
        <div className="min-w-0">
          <p
            className={`truncate font-semibold text-stone-900 ${large ? 'text-lg' : ''}`}
            title={track.title ?? undefined}
          >
            {track.title ?? 'Música fora da biblioteca'}
          </p>
          <p className="truncate text-sm text-stone-500">
            {track.artist ?? 'Artista desconhecido'}
            {store.playlistName && <span className="text-stone-400"> · {store.playlistName}</span>}
          </p>
        </div>
      </div>
      {position !== null && (
        <div className="space-y-1">
          <ProgressBar value={position} max={track.durationMs ?? 0} label="Andamento da música" />
          <div className="flex justify-between text-xs text-stone-500 tabular-nums">
            <span>{formatDuration(position)}</span>
            <span>{formatDuration(track.durationMs)}</span>
          </div>
        </div>
      )}
    </div>
  );
}
