import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { keys } from './queries';
import type { PanelMessage, StoreDetail, StoreSummary } from './types';

export type LiveState = 'connecting' | 'online' | 'offline';

/** Chaves do cache que cada tópico do Hub invalida (D23 d). */
export function topicKeys(topic: string): QueryKey[] {
  const [name, id] = topic.split(':');
  const numeric = id === undefined ? undefined : Number(id);
  switch (name) {
    case 'stores':
      return [keys.stores, ['store']];
    case 'store':
      return numeric === undefined ? [['store']] : [keys.store(numeric)];
    case 'tracks':
      return [keys.tracks, ['storeTracks'], ['playlist']];
    case 'uploads':
      return [keys.uploads];
    case 'playlists':
      return [keys.playlists, ['playlist']];
    case 'playlist':
      return numeric === undefined ? [['playlist']] : [keys.playlist(numeric)];
    case 'offers':
      return [keys.offers, ['offer'], ['storeOffers'], ['playlist']];
    case 'events':
      return numeric === undefined ? [['events']] : [keys.events(numeric)];
    case 'history':
      return numeric === undefined ? [['history']] : [['history', numeric]];
    case 'releases':
      return [keys.releases];
    case 'github':
      return [keys.github];
    default:
      return [];
  }
}

/** Aplica uma mensagem do /ws/panel ao cache do TanStack Query. */
export function applyLiveMessage(client: QueryClient, message: PanelMessage) {
  if (message.type === 'store') {
    const store = message.store;
    const previous = client.getQueryData<StoreDetail>(keys.store(store.id));
    let known = false;
    client.setQueryData<StoreSummary[]>(keys.stores, (list) =>
      list?.map((s) => {
        if (s.id !== store.id) return s;
        known = true;
        return store;
      }),
    );
    client.setQueryData<StoreDetail>(keys.store(store.id), (detail) =>
      detail ? { ...detail, status: store } : detail,
    );
    if (!known && client.getQueryData(keys.stores) !== undefined) {
      void client.invalidateQueries({ queryKey: keys.stores });
    }
    // A fila e as músicas do manifesto vêm só no detalhe; ele é recarregado quando a música ou a versão muda, e
    // enquanto a fila estiver vazia com música tocando (o detalhe pode ter sido lido antes de a loja informar a fila).
    // O Hub também avisa "store:{id}" quando a fila muda. A comparação usa o detalhe de antes da troca do resumo.
    if (
      previous &&
      (previous.queue[0]?.hash === store.nowPlaying?.hash ||
        previous.status.appliedVersion !== store.appliedVersion ||
        (previous.queue.length === 0 && store.nowPlaying !== null))
    ) {
      void client.invalidateQueries({ queryKey: keys.store(store.id) });
    }
    return;
  }

  for (const topic of message.topics) {
    for (const queryKey of topicKeys(topic)) {
      void client.invalidateQueries({ queryKey });
    }
  }
}

export function socketUrl(location: Location): string {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${scheme}//${location.host}/ws/panel`;
}

/**
 * Mantém o WebSocket do painel aberto, reconectando com espera crescente (1 s a 30 s). A cada reconexão o cache
 * inteiro é recarregado, porque avisos podem ter se perdido enquanto estava desconectado.
 */
export function connectLive(client: QueryClient, onState: (state: LiveState) => void): () => void {
  let socket: WebSocket | null = null;
  let stopped = false;
  let attempt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const open = () => {
    if (stopped) return;
    onState('connecting');
    socket = new WebSocket(socketUrl(window.location));
    socket.onopen = () => {
      if (attempt > 0) void client.invalidateQueries();
      attempt = 0;
      onState('online');
    };
    socket.onmessage = (event: MessageEvent<string>) => {
      try {
        applyLiveMessage(client, JSON.parse(event.data) as PanelMessage);
      } catch {
        // Mensagem que esta versão do painel não entende: ignora.
      }
    };
    socket.onclose = () => {
      socket = null;
      if (stopped) return;
      onState('offline');
      const delay = Math.min(30_000, 1000 * 2 ** attempt);
      attempt += 1;
      timer = setTimeout(open, delay);
    };
  };

  open();
  return () => {
    stopped = true;
    clearTimeout(timer);
    socket?.close();
  };
}
