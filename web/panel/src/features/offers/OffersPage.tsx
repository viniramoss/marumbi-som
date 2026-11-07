import { Link, useNavigate } from 'react-router';
import { Clock, Megaphone, Plus } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useOffers, useStores } from '../../api/queries';
import type { OfferSummary, StoreSummary } from '../../api/types';
import { Badge, Button, Card, EmptyState, ErrorNote, PageHeader, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { useOpenOnClick } from '../../hooks/useOpenOnClick';
import { formatDateTime, formatDuration } from '../../lib/format';
import { expiresIn, offerStatus } from './offerDraft';

/** Lista de ofertas com a situação calculada pelo Hub e o aviso de vencimento (seção 7.2, D26 e). */
export function OffersPage() {
  const offers = useOffers();
  const stores = useStores();
  const navigate = useNavigate();
  const open = useOpenOnClick();
  const now = useNow(60_000);
  const expiring = (offers.data ?? []).filter((o) => o.expiresSoon && o.status !== 'expired');

  return (
    <>
      <PageHeader
        title="Ofertas"
        description="Anúncios gravados que tocam por cima da música, com a música abaixada. Nunca tocam sozinhos."
        actions={
          <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => void navigate('/ofertas/nova')}>
            Nova oferta
          </Button>
        }
      />
      {expiring.length > 0 && (
        <p
          role="alert"
          className="mb-4 flex items-center gap-2 rounded-lg bg-amber-50 px-4 py-2.5 text-sm text-amber-900 ring-1 ring-amber-200"
        >
          <Clock className="size-4 shrink-0" aria-hidden />
          {expiring.length === 1
            ? `A oferta "${expiring[0]!.name}" vence nas próximas 24 horas.`
            : `${expiring.length} ofertas vencem nas próximas 24 horas.`}
        </p>
      )}
      <Card>
        {offers.isPending ? (
          <Spinner />
        ) : offers.isError ? (
          <div className="p-5">
            <ErrorNote>{errorMessage(offers.error)}</ErrorNote>
          </div>
        ) : offers.data.length === 0 ? (
          <EmptyState icon={<Megaphone className="size-10" />} title="Nenhuma oferta">
            Envie o áudio gravado, escolha a validade, as lojas e de quanto em quanto tempo ela toca.
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[760px] text-sm">
              <thead>
                <tr className="border-b border-stone-100 text-left text-xs font-medium tracking-wide text-stone-500 uppercase">
                  <th className="px-5 py-2.5">Oferta</th>
                  <th className="py-2.5 pr-4">Validade</th>
                  <th className="py-2.5 pr-4">Lojas</th>
                  <th className="py-2.5 pr-4">Quando toca</th>
                  <th className="py-2.5 pr-5 text-right whitespace-nowrap">Tocou (7 dias)</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-stone-100">
                {offers.data.map((offer) => (
                  <tr
                    key={offer.id}
                    className="cursor-pointer hover:bg-stone-50"
                    onClick={open(`/ofertas/${offer.id}`)}
                  >
                    <td className="px-5 py-3">
                      <Link to={`/ofertas/${offer.id}`} className="font-medium text-stone-900 hover:text-brand-800">
                        {offer.name}
                      </Link>
                      <span className="mt-1 flex flex-wrap items-center gap-1.5">
                        <OfferStatusBadge offer={offer} />
                        {offer.expiresSoon && offer.status !== 'expired' && (
                          <Badge tone="amber">{expiresIn(offer.validUntil, now)}</Badge>
                        )}
                        <span className="text-xs text-stone-500 tabular-nums">{formatDuration(offer.durationMs)}</span>
                      </span>
                    </td>
                    <td className="py-3 pr-4 text-stone-600">
                      <span className="block">{formatDateTime(offer.validFrom)}</span>
                      <span className="block text-xs text-stone-500">até {formatDateTime(offer.validUntil)}</span>
                    </td>
                    <td className="py-3 pr-4 text-stone-700">{storesText(offer, stores.data)}</td>
                    <td className="py-3 pr-4 text-stone-700">{whenText(offer)}</td>
                    <td className="py-3 pr-5 text-right tabular-nums">{offer.plays7d}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}

export function OfferStatusBadge({ offer }: { offer: Pick<OfferSummary, 'status' | 'expiresSoon'> }) {
  const status = offerStatus(offer);
  return (
    <Badge tone={status.tone} dot>
      {status.label}
    </Badge>
  );
}

function storesText(offer: OfferSummary, stores: StoreSummary[] | undefined): string {
  if (offer.storeIds.length === 0) return 'Nenhuma loja';
  if (stores && stores.length > 1 && stores.every((s) => offer.storeIds.includes(s.id))) return 'Todas as lojas';
  const names = (stores ?? []).filter((s) => offer.storeIds.includes(s.id)).map((s) => s.name);
  return names.length > 0 ? names.join(', ') : `${offer.storeIds.length} loja(s)`;
}

function whenText(offer: OfferSummary): string {
  const parts: string[] = [];
  if (offer.ruleCount > 0) parts.push(offer.ruleCount === 1 ? '1 regra' : `${offer.ruleCount} regras`);
  if (offer.anchorCount > 0) {
    parts.push(offer.anchorCount === 1 ? 'presa em 1 música' : `presa em ${offer.anchorCount} músicas`);
  }
  return parts.length > 0 ? parts.join(' · ') : 'Só pelo botão "tocar oferta agora"';
}
