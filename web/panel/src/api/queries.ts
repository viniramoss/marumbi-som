import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, call } from './client';
import type {
  CommandOutcome,
  ItemInput,
  PlaylistDetail,
  ScheduleInput,
  StoreDetail,
  StoreInput,
  StoreSummary,
  Track,
} from './types';

/** Chaves do cache. O WebSocket do painel (live.ts) invalida por estas chaves. */
export const keys = {
  session: ['session'] as const,
  stores: ['stores'] as const,
  store: (id: number) => ['store', id] as const,
  storeTracks: (id: number) => ['storeTracks', id] as const,
  history: (id: number, since: string) => ['history', id, since] as const,
  events: (id: number) => ['events', id] as const,
  tracks: ['tracks'] as const,
  uploads: ['uploads'] as const,
  playlists: ['playlists'] as const,
  playlist: (id: number) => ['playlist', id] as const,
  settings: ['settings'] as const,
};

export function useSession() {
  return useQuery({
    queryKey: keys.session,
    queryFn: async () => {
      const { data, response } = await api.GET('/api/auth/me');
      return response.ok && data ? data : null;
    },
    staleTime: Infinity,
    retry: false,
  });
}

export function useStores() {
  return useQuery({ queryKey: keys.stores, queryFn: () => call(api.GET('/api/stores')) });
}

export function useStore(id: number) {
  return useQuery({
    queryKey: keys.store(id),
    queryFn: () => call(api.GET('/api/stores/{id}', { params: { path: { id } } })),
  });
}

export function useStoreTracks(id: number, enabled: boolean) {
  return useQuery({
    queryKey: keys.storeTracks(id),
    queryFn: () => call(api.GET('/api/stores/{id}/tracks', { params: { path: { id } } })),
    enabled,
  });
}

export function useHistory(id: number, since: string) {
  return useQuery({
    queryKey: keys.history(id, since),
    queryFn: () =>
      call(api.GET('/api/stores/{id}/history', { params: { path: { id }, query: { since, limit: 2000 } } })),
  });
}

export function useEvents(id: number) {
  return useQuery({
    queryKey: keys.events(id),
    queryFn: () => call(api.GET('/api/stores/{id}/events', { params: { path: { id }, query: { limit: 200 } } })),
  });
}

export function useTracks() {
  return useQuery({ queryKey: keys.tracks, queryFn: () => call(api.GET('/api/tracks')) });
}

export function useUploads() {
  return useQuery({ queryKey: keys.uploads, queryFn: () => call(api.GET('/api/uploads')) });
}

export function usePlaylists() {
  return useQuery({ queryKey: keys.playlists, queryFn: () => call(api.GET('/api/playlists')) });
}

export function usePlaylist(id: number) {
  return useQuery({
    queryKey: keys.playlist(id),
    queryFn: () => call(api.GET('/api/playlists/{id}', { params: { path: { id } } })),
  });
}

export function useSettings() {
  return useQuery({ queryKey: keys.settings, queryFn: () => call(api.GET('/api/settings')) });
}

/** Grava o detalhe devolvido pelo Hub no cache da loja e no resumo da lista. */
function storeSaved(client: ReturnType<typeof useQueryClient>, detail: StoreDetail) {
  client.setQueryData(keys.store(detail.status.id), detail);
  client.setQueryData<StoreSummary[]>(keys.stores, (list) =>
    list?.map((s) => (s.id === detail.status.id ? detail.status : s)),
  );
}

export function useCreateStore() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => call(api.POST('/api/stores', { body: { name } })),
    onSuccess: (detail) => {
      storeSaved(client, detail);
      void client.invalidateQueries({ queryKey: keys.stores });
    },
  });
}

export function useUpdateStore(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: StoreInput) => call(api.PATCH('/api/stores/{id}', { params: { path: { id } }, body: input })),
    onSuccess: (detail) => storeSaved(client, detail),
  });
}

export function useSaveSchedules(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (schedules: ScheduleInput[]) =>
      call(api.PUT('/api/stores/{id}/schedules', { params: { path: { id } }, body: schedules })),
    onSuccess: (detail) => {
      storeSaved(client, detail);
      void client.invalidateQueries({ queryKey: keys.playlists });
    },
  });
}

export function useDeleteStore() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => call(api.DELETE('/api/stores/{id}', { params: { path: { id } } })),
    onSuccess: (_, id) => {
      client.removeQueries({ queryKey: keys.store(id) });
      void client.invalidateQueries({ queryKey: keys.stores });
      void client.invalidateQueries({ queryKey: keys.playlists });
    },
  });
}

export function useEnrollCode(id: number) {
  return useMutation({
    mutationFn: () => call(api.POST('/api/stores/{id}/enroll-code', { params: { path: { id } } })),
  });
}

export function useRevokeToken(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => call(api.DELETE('/api/stores/{id}/token', { params: { path: { id } } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: keys.store(id) }),
  });
}

export type StoreCommand =
  | { command: 'play' | 'pause' | 'next' | 'reconnectBluetooth' | 'restartAgent' }
  | { command: 'setVolume'; volume: number }
  | { command: 'playNow'; hash: string };

export function sendCommand(id: number, command: StoreCommand): Promise<CommandOutcome> {
  const args =
    command.command === 'setVolume'
      ? { volume: command.volume }
      : command.command === 'playNow'
        ? { hash: command.hash }
        : null;
  return call(
    api.POST('/api/stores/{id}/commands', { params: { path: { id } }, body: { command: command.command, args } }),
  );
}

export function useUpdateTrack() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ hash, title, artist }: { hash: string; title: string; artist: string }) =>
      call(api.PATCH('/api/tracks/{hash}', { params: { path: { hash } }, body: { title, artist } })),
    onSuccess: (track) => {
      client.setQueryData<Track[]>(keys.tracks, (list) => list?.map((t) => (t.hash === track.hash ? track : t)));
      void client.invalidateQueries({ queryKey: ['playlist'] });
    },
  });
}

export function useDeleteTrack() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (hash: string) => call(api.DELETE('/api/tracks/{hash}', { params: { path: { hash } } })),
    onSuccess: (_, hash) => client.setQueryData<Track[]>(keys.tracks, (list) => list?.filter((t) => t.hash !== hash)),
  });
}

export function useCreatePlaylist() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => call(api.POST('/api/playlists', { body: { name, shuffle: false } })),
    onSuccess: (playlist) => {
      client.setQueryData(keys.playlist(playlist.id), playlist);
      void client.invalidateQueries({ queryKey: keys.playlists });
    },
  });
}

/** Salvar = publicar (seção 7.1): renomeia/alterna aleatório se mudou e substitui a lista inteira. */
export function useSavePlaylist(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({
      name,
      shuffle,
      items,
      changedSettings,
    }: {
      name: string;
      shuffle: boolean;
      items: ItemInput[];
      changedSettings: boolean;
    }): Promise<PlaylistDetail> => {
      if (changedSettings) {
        await call(api.PATCH('/api/playlists/{id}', { params: { path: { id } }, body: { name, shuffle } }));
      }
      return call(api.PUT('/api/playlists/{id}/items', { params: { path: { id } }, body: items }));
    },
    onSuccess: (playlist) => {
      client.setQueryData(keys.playlist(id), playlist);
      void client.invalidateQueries({ queryKey: keys.playlists });
      void client.invalidateQueries({ queryKey: keys.tracks });
    },
  });
}

export function useDeletePlaylist() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => call(api.DELETE('/api/playlists/{id}', { params: { path: { id } } })),
    onSuccess: (_, id) => {
      client.removeQueries({ queryKey: keys.playlist(id) });
      void client.invalidateQueries({ queryKey: keys.playlists });
      void client.invalidateQueries({ queryKey: keys.tracks });
    },
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      call(api.POST('/api/auth/password', { body })),
  });
}
