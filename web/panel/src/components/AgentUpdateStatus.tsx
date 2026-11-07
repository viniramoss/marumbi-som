import type { UpdateInfo } from '../api/types';
import { formatBytes } from '../lib/format';
import { Badge, ProgressBar } from './ui';

/** Andamento e resultado da atualização do agente numa loja (D27 h): barra enquanto baixa, conferindo ou instalando. */
export function AgentUpdateStatus({ update, className = '' }: { update: UpdateInfo | null; className?: string }) {
  if (!update) return null;
  const version = update.version;
  switch (update.stage) {
    case 'downloading': {
      const total = update.totalBytes ?? 0;
      return (
        <div className={`space-y-1 ${className}`}>
          <p className="text-xs text-stone-600">
            Baixando o agente {version}… {formatBytes(update.doneBytes ?? 0)} de {formatBytes(total)}
          </p>
          <ProgressBar value={update.doneBytes ?? 0} max={total} label={`Baixando o agente ${version}`} />
        </div>
      );
    }
    case 'verifying':
    case 'installing':
      return (
        <div className={`space-y-1 ${className}`}>
          <p className="text-xs text-stone-600">
            {update.stage === 'verifying'
              ? `Conferindo a assinatura do agente ${version}…`
              : `Instalando o agente ${version}: a música para por alguns segundos.`}
          </p>
          <ProgressBar label={`Atualizando o agente para ${version}`} />
        </div>
      );
    case 'done':
      return (
        <div className={className}>
          <Badge tone="green">Agente atualizado para {version}</Badge>
        </div>
      );
    default:
      return (
        <div className={`space-y-1 ${className}`}>
          <Badge tone={update.stage === 'rolledBack' ? 'amber' : 'red'}>
            {update.stage === 'rolledBack' ? `Versão ${version} revertida` : `Atualização para ${version} falhou`}
          </Badge>
          {update.error && <p className="text-xs text-stone-600">{update.error}</p>}
        </div>
      );
  }
}
