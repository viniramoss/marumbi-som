import { useState } from 'react';
import { errorMessage } from '../api/client';
import { sendCommand, type StoreCommand } from '../api/queries';
import { useToast } from '../components/Toasts';

const done: Record<StoreCommand['command'], string> = {
  play: 'Tocando.',
  pause: 'Pausado.',
  next: 'Pulando para a próxima música.',
  setVolume: 'Volume ajustado.',
  playNow: 'A música escolhida entra agora.',
  playOffer: 'A oferta entra agora, por cima da música.',
  reconnectBluetooth: 'Pedido de reconexão da caixa enviado.',
  restartAudio: 'A loja está reiniciando o áudio. A música volta sozinha em cerca de 30 s.',
  restartAgent: 'O agente da loja está reiniciando.',
  updateAgent: 'A loja começou a baixar a versão nova do agente.',
};

/**
 * Comando ao vivo para uma loja (seção 7.2). O Hub espera até 5 s pela resposta; o estado novo chega pelo
 * WebSocket do painel logo depois (meta de 2 s na Etapa 4).
 */
export function useCommand(storeId: number) {
  const toast = useToast();
  const [busy, setBusy] = useState<StoreCommand['command'] | null>(null);

  const run = async (command: StoreCommand, quiet = false) => {
    setBusy(command.command);
    try {
      const outcome = await sendCommand(storeId, command);
      switch (outcome.result) {
        case 'ok':
          if (!quiet) toast.success(done[command.command]);
          return true;
        case 'offline':
          toast.error('A loja está desconectada do Hub.');
          return false;
        case 'pending':
          toast.info('A loja não respondeu em 5 s. O resultado aparece quando ela responder.');
          return false;
        default:
          toast.error(outcome.error ?? 'A loja recusou o comando.');
          return false;
      }
    } catch (error) {
      toast.error(errorMessage(error));
      return false;
    } finally {
      setBusy(null);
    }
  };

  return { run, busy };
}
