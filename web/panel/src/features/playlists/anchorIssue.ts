import type { OfferSummary, PlaylistUsage } from '../../api/types';
import { formatDateTime } from '../../lib/format';

/** Por que uma oferta presa na playlist não toca agora. */
export interface AnchorIssue {
  /** Etiqueta curta no rótulo da oferta ("Vencida"). */
  label: string;
  /** Explicação com o que fazer, para o aviso da playlist e a dica do rótulo. */
  detail: string;
}

/**
 * A âncora continua na playlist quando a oferta vence, é desativada ou não vale para nenhuma loja que toca a playlist,
 * mas o Hub deixa essa oferta fora do manifesto: o rótulo mostrava a oferta como se fosse tocar. Oferta que vale só
 * para parte das lojas da playlist não é problema (ex.: oferta só da Loja 3).
 */
export function anchorIssue(offer: OfferSummary | undefined, usage: readonly PlaylistUsage[]): AnchorIssue | null {
  if (!offer) return null;
  if (!offer.enabled || offer.status === 'disabled') {
    return { label: 'Desativada', detail: 'Está desativada e não toca. Abra a oferta e marque Ativa.' };
  }
  if (offer.status === 'expired') {
    return {
      label: 'Vencida',
      detail: `Venceu em ${formatDateTime(offer.validUntil)} e não toca mais. Em Ofertas, marque e use Prorrogar, ou abra a oferta e mude o Termina.`,
    };
  }
  const playlistStores = new Map(usage.map((u) => [u.storeId, u.storeName]));
  if (offer.storeIds.length > 0 && playlistStores.size > 0 && !offer.storeIds.some((id) => playlistStores.has(id))) {
    return {
      label: 'Fora das lojas',
      detail: `Não está marcada para ${[...playlistStores.values()].join(', ')}, que tocam esta playlist. Abra a oferta e marque a loja.`,
    };
  }
  if (offer.status === 'scheduled') {
    return { label: 'Agendada', detail: `Começa a tocar em ${formatDateTime(offer.validFrom)}.` };
  }
  return null;
}

/** Problema de cada oferta presa, pelo id da oferta. */
export function anchorIssues(
  offerIds: readonly number[],
  offers: readonly OfferSummary[],
  usage: readonly PlaylistUsage[],
): Map<number, AnchorIssue> {
  const issues = new Map<number, AnchorIssue>();
  for (const id of new Set(offerIds)) {
    const issue = anchorIssue(
      offers.find((o) => o.id === id),
      usage,
    );
    if (issue) issues.set(id, issue);
  }
  return issues;
}
