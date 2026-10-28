import type { StoreSummary } from '../api/types';

export type Tone = 'green' | 'amber' | 'red' | 'gray' | 'blue';

export interface StatusLabel {
  label: string;
  tone: Tone;
}

/** Situação geral da loja para o selo do cartão. */
export function playerStatus(store: StoreSummary): StatusLabel {
  if (!store.enrolled) return { label: 'Não vinculada', tone: 'gray' };
  if (!store.online) return { label: 'Desconectada', tone: 'red' };
  switch (store.player) {
    case 'playing':
      return { label: 'Tocando', tone: 'green' };
    case 'paused':
      return { label: 'Pausada', tone: 'amber' };
    default:
      // Sem nenhuma faixa na agenda a loja fica em silêncio o tempo todo; quem vincula nem sempre sabe disso.
      if (!store.hasSchedule) return { label: 'Sem agenda', tone: 'amber' };
      return { label: store.open ? 'Parada' : 'Fora do horário', tone: 'gray' };
  }
}

export function bluetoothStatus(store: StoreSummary): StatusLabel {
  switch (store.bluetooth) {
    case 'connected':
      return { label: 'Caixa conectada', tone: 'green' };
    case 'connecting':
      return { label: 'Conectando a caixa…', tone: 'amber' };
    case 'radioOff':
      return { label: 'Bluetooth desligado', tone: 'red' };
    case 'disconnected':
      return { label: 'Caixa desconectada', tone: 'red' };
    default:
      return { label: 'Caixa: sem informação', tone: 'gray' };
  }
}

/** "Sincronizado ✅", "Baixando 3/10", "Aguardando a loja". */
export function syncStatus(store: StoreSummary): StatusLabel {
  if (store.latestVersion === null) return { label: 'Sem configuração', tone: 'gray' };
  if (store.pendingVersion !== null && store.syncTotal !== null) {
    return { label: `Baixando ${store.syncDone ?? 0}/${store.syncTotal}`, tone: 'blue' };
  }
  if (store.appliedVersion !== null && store.appliedVersion >= store.latestVersion) {
    return { label: 'Sincronizado ✅', tone: 'green' };
  }
  if (!store.online) return { label: `Versão ${store.latestVersion} aguardando a loja`, tone: 'amber' };
  return { label: `Aplicando a versão ${store.latestVersion}…`, tone: 'blue' };
}

/** Se a loja já aplicou a última alteração (seção 7.1: toda tela mostra isso). */
export function appliedStatus(store: StoreSummary): StatusLabel {
  const sync = syncStatus(store);
  if (sync.tone === 'green') return { label: 'Aplicado', tone: 'green' };
  if (!store.online) return { label: 'Desconectada: aplica quando voltar', tone: 'amber' };
  return sync;
}

export const canControl = (store: StoreSummary) => store.online;
