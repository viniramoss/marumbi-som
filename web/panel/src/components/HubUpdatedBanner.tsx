import { useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { useHubUpdate } from '../api/queries';
import { Button } from './ui';

/**
 * Depois que o Hub troca de versão (D32), a página aberta ainda é a do painel antigo: avisa para recarregar. Compara
 * com a primeira versão vista nesta aba; a reconexão do "Ao vivo" recarrega a situação quando o Hub volta.
 */
export function HubUpdatedBanner() {
  const status = useHubUpdate();
  const version = status.data?.currentVersion;
  const [first, setFirst] = useState(version);
  if (first === undefined && version !== undefined) setFirst(version);
  if (first === undefined || version === undefined || version === first) return null;

  return (
    <div
      role="status"
      className="mb-5 flex flex-wrap items-center justify-between gap-3 rounded-lg bg-brand-50 px-4 py-3 text-sm text-brand-900 ring-1 ring-brand-200"
    >
      <p>
        O Hub foi atualizado para a versão <strong className="font-semibold">{version}</strong>. Recarregue a página
        para usar o painel novo.
      </p>
      <Button variant="primary" icon={<RefreshCw className="size-4" />} onClick={() => window.location.reload()}>
        Recarregar
      </Button>
    </div>
  );
}
