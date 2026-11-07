import { QueryClient } from '@tanstack/react-query';
import { storeDetail, storeSummary } from '../test/fixtures';
import { applyLiveMessage, socketUrl, topicKeys } from './live';
import { keys } from './queries';

describe('applyLiveMessage', () => {
  it('troca o resumo da loja na lista e no detalhe sem recarregar', () => {
    const client = new QueryClient();
    client.setQueryData(keys.stores, [storeSummary(), storeSummary({ id: 2, name: 'Marumbi Água Verde' })]);
    client.setQueryData(keys.store(1), storeDetail());
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    applyLiveMessage(client, { type: 'store', store: storeSummary({ volume: 40, player: 'paused' }) });

    expect(client.getQueryData<ReturnType<typeof storeSummary>[]>(keys.stores)?.[0]).toMatchObject({
      volume: 40,
      player: 'paused',
    });
    expect(client.getQueryData<ReturnType<typeof storeDetail>>(keys.store(1))?.status.volume).toBe(40);
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('loja que a lista não conhece faz recarregar a lista', () => {
    const client = new QueryClient();
    client.setQueryData(keys.stores, [storeSummary()]);
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    applyLiveMessage(client, { type: 'store', store: storeSummary({ id: 9 }) });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: keys.stores });
  });

  it('a fila do detalhe é recarregada quando a música da fila começa', () => {
    const client = new QueryClient();
    client.setQueryData(keys.store(1), storeDetail());
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const next = storeSummary({ nowPlaying: { ...storeSummary().nowPlaying!, hash: 'h2' } });

    applyLiveMessage(client, { type: 'store', store: next });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: keys.store(1) });
  });

  it('a fila do detalhe é recarregada quando a loja aplica uma versão nova da configuração', () => {
    const client = new QueryClient();
    client.setQueryData(keys.store(1), storeDetail());
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    applyLiveMessage(client, { type: 'store', store: storeSummary({ appliedVersion: 4, latestVersion: 4 }) });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: keys.store(1) });
  });

  it('fila vazia com música tocando é recarregada (o detalhe foi lido antes de a loja informar a fila)', () => {
    const client = new QueryClient();
    client.setQueryData(keys.store(1), storeDetail({ queue: [] }));
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    applyLiveMessage(client, { type: 'store', store: storeSummary() });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: keys.store(1) });
  });

  it('aviso de mudança invalida as chaves do tópico', () => {
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    applyLiveMessage(client, { type: 'changed', topics: ['playlist:4', 'history:1'] });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: keys.playlist(4) });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['history', 1] });
  });
});

describe('topicKeys', () => {
  it('cobre os tópicos do Hub (D23 d) e ignora os desconhecidos', () => {
    expect(topicKeys('stores')).toEqual([keys.stores, ['store']]);
    expect(topicKeys('store:3')).toEqual([keys.store(3)]);
    expect(topicKeys('tracks')).toEqual([keys.tracks, ['storeTracks'], ['playlist']]);
    expect(topicKeys('uploads')).toEqual([keys.uploads]);
    expect(topicKeys('events:2')).toEqual([keys.events(2)]);
    expect(topicKeys('offers')).toEqual([keys.offers, ['offer'], ['storeOffers'], ['playlist']]);
    expect(topicKeys('releases')).toEqual([keys.releases]);
    expect(topicKeys('github')).toEqual([keys.github]);
    expect(topicKeys('novidade')).toEqual([]);
  });
});

describe('socketUrl', () => {
  it('usa wss atrás do HTTPS do Tailscale', () => {
    expect(socketUrl({ protocol: 'https:', host: 'hub.tail1234.ts.net' } as Location)).toBe(
      'wss://hub.tail1234.ts.net/ws/panel',
    );
    expect(socketUrl({ protocol: 'http:', host: '127.0.0.1:5080' } as Location)).toBe('ws://127.0.0.1:5080/ws/panel');
  });
});
