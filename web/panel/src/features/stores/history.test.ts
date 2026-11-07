import type { HistoryEntry } from '../../api/types';
import { dayCounts, groupByDay } from './history';

const entry = (startedAt: string, kind: HistoryEntry['kind'] = 'music'): HistoryEntry => ({
  trackHash: kind === 'music' ? 'h1' : null,
  startedAt,
  title: kind === 'music' ? 'Evidências' : 'Picanha',
  artist: null,
  kind,
  result: kind === 'offer' ? 'played' : null,
});

describe('history', () => {
  it('agrupa por dia e conta músicas e ofertas separadas', () => {
    const groups = groupByDay([
      entry('2026-10-23T15:00:00-03:00'),
      entry('2026-10-23T14:58:00-03:00', 'offer'),
      entry('2026-10-23T14:55:00-03:00'),
      entry('2026-10-22T10:00:00-03:00'),
    ]);
    expect(groups).toHaveLength(2);
    expect(dayCounts(groups[0]!.entries)).toBe('2 músicas · 1 oferta');
    expect(dayCounts(groups[1]!.entries)).toBe('1 música');
  });
});
