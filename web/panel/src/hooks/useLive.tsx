import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { connectLive, type LiveState } from '../api/live';

const LiveContext = createContext<LiveState>('connecting');

/** Abre o WebSocket do painel enquanto há sessão e expõe o estado da conexão (indicador "Ao vivo"). */
export function LiveProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const [state, setState] = useState<LiveState>('connecting');
  useEffect(() => connectLive(client, setState), [client]);
  return <LiveContext.Provider value={state}>{children}</LiveContext.Provider>;
}

export const useLiveState = () => useContext(LiveContext);
