import type { NowPlayingInfo } from '../api/types';

/**
 * Posição atual da música, andando a partir da última posição informada pela loja (o status chega a cada 30 s ou a
 * cada mudança). Só anda enquanto toca e nunca passa da duração.
 */
export function currentPositionMs(
  nowPlaying: NowPlayingInfo | null,
  player: string | null,
  now: number,
): number | null {
  if (!nowPlaying || nowPlaying.positionMs === null) return null;
  let position = nowPlaying.positionMs;
  if (player === 'playing' && nowPlaying.positionAt) {
    position += Math.max(0, now - Date.parse(nowPlaying.positionAt));
  }
  if (nowPlaying.durationMs !== null) position = Math.min(position, nowPlaying.durationMs);
  return Math.max(0, position);
}
