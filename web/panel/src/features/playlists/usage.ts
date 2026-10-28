import type { PlaylistUsage } from '../../api/types';
import { formatDays } from '../../lib/format';

/** "Seg a Sex, 07:00–24:00": onde a playlist aparece na agenda de uma loja. */
export function usageText(usage: PlaylistUsage): string {
  return `${formatDays(usage.days)}, ${usage.start}–${usage.end}`;
}
