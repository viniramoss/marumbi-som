import type { Track } from '../../api/types';

/** Segunda linha de uma música: o artista, se tiver, e o nome do arquivo enviado em letra menor (D31). */
export function TrackSubtitle({ track }: { track: Pick<Track, 'artist' | 'originalName'> }) {
  return (
    <span className="block truncate text-xs text-stone-500" title={track.originalName}>
      {track.artist && <span>{track.artist} · </span>}
      <span className="text-stone-400">{track.originalName}</span>
    </span>
  );
}
