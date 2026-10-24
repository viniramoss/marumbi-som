import { Link } from 'react-router';
import { Download } from 'lucide-react';
import { useReleases } from '../../api/queries';
import type { StoreSummary } from '../../api/types';
import { storesToUpdate } from '../../lib/agentUpdate';

/** "Versão X disponível" no Início quando há lojas conectadas numa versão anterior à de distribuição (D29). */
export function AgentUpdateNotice({ stores }: { stores: readonly StoreSummary[] }) {
  const releases = useReleases();
  const current = releases.data?.find((r) => r.isCurrent);
  const outdated = storesToUpdate(stores, current);
  if (!current || outdated.length === 0) return null;
  return (
    <Link
      to="/configuracoes"
      className="mb-5 flex flex-wrap items-center gap-x-3 gap-y-1 rounded-xl bg-brand-50 px-4 py-3 text-sm text-brand-900 ring-1 ring-brand-200 transition-colors hover:bg-brand-100"
    >
      <Download className="size-4 shrink-0" aria-hidden />
      <span className="font-medium">Versão {current.version} do agente disponível.</span>
      <span>
        {outdated.length === 1
          ? `1 loja numa versão anterior (${outdated[0]!.name}).`
          : `${outdated.length} lojas numa versão anterior.`}
      </span>
      <span className="ml-auto font-medium underline">Atualizar em Configurações</span>
    </Link>
  );
}
