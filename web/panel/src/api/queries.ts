import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, call } from './client';
import type {
  AnchorInput,
  CommandOutcome,
  ItemInput,
  OfferDetail,
  OfferInput,
  OfferSummary,
  PlaylistDetail,
  ReleaseInfo,
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
  offers: ['offers'] as const,
  offer: (id: number) => ['offer', id] as const,
  offerAudio: ['offerAudio'] as const,
  storeOffers: (id: number) => ['storeOffers', id] as const,
  releases: ['releases'] as const,
  github: ['github'] as const,
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

/** `refetchInterval`: consulta de tempos em tempos enquanto algo processa (caso o WebSocket do painel caia). */
export function useUploads(refetchInterval: number | false = false) {
  return useQuery({ queryKey: keys.uploads, queryFn: () => call(api.GET('/api/uploads')), refetchInterval });
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

export function useOffers() {
  return useQuery({ queryKey: keys.offers, queryFn: () => call(api.GET('/api/offers')) });
}

export function useOffer(id: number, enabled = true) {
  return useQuery({
    queryKey: keys.offer(id),
    queryFn: () => call(api.GET('/api/offers/{id}', { params: { path: { id } } })),
    enabled,
  });
}

/** Áudios de oferta já processados (para a duração do áudio recém-enviado, antes de salvar a oferta). */
export function useOfferAudio() {
  return useQuery({
    queryKey: keys.offerAudio,
    queryFn: () => call(api.GET('/api/tracks', { params: { query: { kind: 'offer' } } })),
  });
}

/** Ofertas que valem agora na configuração aplicada da loja, para "tocar oferta agora" (D26 g). */
export function useStoreOffers(id: number, enabled: boolean) {
  return useQuery({
    queryKey: keys.storeOffers(id),
    queryFn: () => call(api.GET('/api/stores/{id}/offers', { params: { path: { id } } })),
    enabled,
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
  | { command: 'playNow'; hash: string }
  | { command: 'playOffer'; offerKey: string }
  // Sem versão, o Hub manda a versão para distribuição (D27 d).
  | { command: 'updateAgent'; version?: string };

function commandArgs(command: StoreCommand) {
  switch (command.command) {
    case 'setVolume':
      return { volume: command.volume };
    case 'playNow':
      return { hash: command.hash };
    case 'playOffer':
      // O agente conhece a oferta pelo identificador do manifesto (of-12).
      return { offerId: command.offerKey };
    case 'updateAgent':
      return command.version ? { version: command.version } : null;
    default:
      return null;
  }
}

export function sendCommand(id: number, command: StoreCommand): Promise<CommandOutcome> {
  const args = commandArgs(command);
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

/**
 * Salvar = publicar (seção 7.1): renomeia/alterna aleatório se mudou e substitui a lista inteira. As âncoras de
 * ofertas vão depois dos itens, já com os ids dos itens novos (D26 c): `anchors` recebe a playlist salva e devolve as
 * âncoras a gravar, ou nulo para não mexer nelas.
 */
export function useSavePlaylist(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({
      name,
      shuffle,
      items,
      changedSettings,
      anchors,
    }: {
      name: string;
      shuffle: boolean;
      items: ItemInput[];
      changedSettings: boolean;
      anchors?: (saved: PlaylistDetail) => AnchorInput[] | null;
    }): Promise<PlaylistDetail> => {
      if (changedSettings) {
        await call(api.PATCH('/api/playlists/{id}', { params: { path: { id } }, body: { name, shuffle } }));
      }
      const saved = await call(api.PUT('/api/playlists/{id}/items', { params: { path: { id } }, body: items }));
      const body = anchors?.(saved);
      if (!body) return saved;
      return call(api.PUT('/api/playlists/{id}/anchors', { params: { path: { id } }, body }));
    },
    onSuccess: (playlist) => {
      client.setQueryData(keys.playlist(id), playlist);
      void client.invalidateQueries({ queryKey: keys.playlists });
      void client.invalidateQueries({ queryKey: keys.tracks });
      void client.invalidateQueries({ queryKey: keys.offers });
      void client.invalidateQueries({ queryKey: ['offer'] });
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

/** Grava a oferta devolvida pelo Hub no cache do detalhe e da lista. */
function offerSaved(client: ReturnType<typeof useQueryClient>, detail: OfferDetail) {
  client.setQueryData(keys.offer(detail.offer.id), detail);
  client.setQueryData<OfferSummary[]>(keys.offers, (list) =>
    list?.some((o) => o.id === detail.offer.id)
      ? list.map((o) => (o.id === detail.offer.id ? detail.offer : o))
      : list && [detail.offer, ...list],
  );
  void client.invalidateQueries({ queryKey: keys.offers });
}

export function useCreateOffer() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: OfferInput) => call(api.POST('/api/offers', { body: input })),
    onSuccess: (detail) => offerSaved(client, detail),
  });
}

export function useUpdateOffer(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: OfferInput) => call(api.PATCH('/api/offers/{id}', { params: { path: { id } }, body: input })),
    onSuccess: (detail) => offerSaved(client, detail),
  });
}

export function useDeleteOffer() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => call(api.DELETE('/api/offers/{id}', { params: { path: { id } } })),
    onSuccess: (_, id) => {
      client.removeQueries({ queryKey: keys.offer(id) });
      client.setQueryData<OfferSummary[]>(keys.offers, (list) => list?.filter((o) => o.id !== id));
      void client.invalidateQueries({ queryKey: keys.offers });
      void client.invalidateQueries({ queryKey: ['playlist'] });
    },
  });
}

export function useReleases() {
  return useQuery({ queryKey: keys.releases, queryFn: () => call(api.GET('/api/releases')) });
}

export function useSetCurrentRelease() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (version: string) => call(api.PUT('/api/releases/current', { body: { version } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: keys.releases }),
  });
}

export function useDeleteRelease() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (version: string) => call(api.DELETE('/api/releases/{version}', { params: { path: { version } } })),
    onSuccess: (_, version) => {
      client.setQueryData<ReleaseInfo[]>(keys.releases, (list) => list?.filter((r) => r.version !== version));
      void client.invalidateQueries({ queryKey: keys.releases });
    },
  });
}

export function useGitHubStatus() {
  return useQuery({ queryKey: keys.github, queryFn: () => call(api.GET('/api/releases/github')) });
}

/** "Procurar agora": o Hub responde na hora e avisa o andamento pelo painel ao vivo (tópico github). */
export function useCheckGitHub() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => call(api.POST('/api/releases/github/check')),
    onSuccess: (status) => client.setQueryData(keys.github, status),
  });
}

/** Cadastra (ou apaga, com null) o token do GitHub; o Hub nunca devolve o token. */
export function useSaveGitHubToken() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (token: string | null) => call(api.PUT('/api/releases/github/token', { body: { token } })),
    onSuccess: (status) => client.setQueryData(keys.github, status),
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      call(api.POST('/api/auth/password', { body })),
  });
}
