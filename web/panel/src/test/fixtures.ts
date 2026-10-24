import type { PlaylistDetail, PlaylistSummary, StoreDetail, StoreSummary } from '../api/types';

/** Dados de exemplo no formato exato do Hub, para os testes. */
export function storeSummary(overrides: Partial<StoreSummary> = {}): StoreSummary {
  return {
    id: 1,
    slug: 'centro',
    name: 'Marumbi Centro',
    online: true,
    enrolled: true,
    latestVersion: 3,
    appliedVersion: 3,
    pendingVersion: null,
    syncDone: null,
    syncTotal: null,
    player: 'playing',
    nowPlaying: {
      hash: 'h1',
      title: 'Evidências',
      artist: 'Chitãozinho & Xororó',
      durationMs: 240_000,
      positionMs: 60_000,
      positionAt: '2026-10-23T13:00:00Z',
    },
    bluetooth: 'connected',
    volume: 70,
    diskFreeMb: 50_000,
    agentVersion: '0.1.0',
    lastSeenAt: '2026-10-23T13:00:00Z',
    playlistId: 'p1',
    playlistName: 'Manhã',
    open: true,
    hasSchedule: true,
    minOfferGapS: 300,
    nextOffer: null,
    lastOffer: null,
    update: null,
    alerts: [],
    ...overrides,
  };
}

export function storeDetail(overrides: Partial<StoreDetail> = {}): StoreDetail {
  return {
    status: storeSummary(),
    volume: 70,
    crossfadeS: 5,
    minOfferGapS: 300,
    timezone: 'America/Sao_Paulo',
    btDeviceName: null,
    schedules: [{ days: [1, 2, 3, 4, 5, 6], start: '07:00', end: '24:00', playlistId: 1 }],
    queue: [{ hash: 'h2', title: 'Fio de Cabelo', artist: null, durationMs: 200_000 }],
    ...overrides,
  };
}

export function playlistSummary(overrides: Partial<PlaylistSummary> = {}): PlaylistSummary {
  return {
    id: 1,
    name: 'Manhã',
    shuffle: false,
    itemCount: 2,
    durationMs: 440_000,
    updatedAt: '2026-10-23T12:00:00Z',
    usage: [],
    ...overrides,
  };
}

export function playlistDetail(overrides: Partial<PlaylistDetail> = {}): PlaylistDetail {
  return {
    id: 1,
    name: 'Manhã',
    shuffle: false,
    items: [
      { id: 'item-1', hash: 'h1', title: 'Evidências', artist: 'Chitãozinho & Xororó', durationMs: 240_000 },
      { id: 'item-2', hash: 'h2', title: 'Fio de Cabelo', artist: null, durationMs: 200_000 },
    ],
    usage: [],
    anchors: [],
    ...overrides,
  };
}
