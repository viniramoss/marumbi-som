import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { CalendarClock, CalendarPlus, Clock, Megaphone, Plus, X } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useChangeOfferValidity, useOffers, useStores } from '../../api/queries';
import type { OfferSummary, OfferValidityInput, StoreSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Badge, Button, Card, EmptyState, ErrorNote, Field, Input, PageHeader, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { useOpenOnClick } from '../../hooks/useOpenOnClick';
import { formatDateTime, formatDuration } from '../../lib/format';
import { DefaultValidityDays, endOfDayAfter, expiresIn, fromLocalInput, offerStatus, toLocalInput } from './offerDraft';

/** Botões de prorrogar da seleção (D39). */
const ExtendOptions = [15, 30] as const;

/** Lista de ofertas com a situação calculada pelo Hub e o aviso de vencimento (seção 7.2, D26 e). */
export function OffersPage() {
  const offers = useOffers();
  const stores = useStores();
  const navigate = useNavigate();
  const open = useOpenOnClick();
  const now = useNow(60_000);
  const expiring = (offers.data ?? []).filter((o) => o.expiresSoon && o.status !== 'expired');
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  // Oferta removida em outra aba sai da seleção sozinha.
  const chosen = (offers.data ?? []).filter((o) => selected.has(o.id));
  const allChosen = offers.data !== undefined && offers.data.length > 0 && chosen.length === offers.data.length;
  const toggle = (id: number) =>
    setSelected((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });

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
          <>
            <SelectionBar offers={offers.data} chosen={chosen} onSelect={(ids) => setSelected(new Set(ids))} />
            <div className="overflow-x-auto">
              <table className="w-full min-w-[800px] text-sm">
                <thead>
                  <tr className="border-b border-stone-100 text-left text-xs font-medium tracking-wide text-stone-500 uppercase">
                    <th className="w-10 py-2.5 pl-5">
                      <input
                        type="checkbox"
                        aria-label="Selecionar todas as ofertas"
                        checked={allChosen}
                        ref={(el) => {
                          if (el) el.indeterminate = chosen.length > 0 && !allChosen;
                        }}
                        onChange={() => setSelected(new Set(allChosen ? [] : offers.data.map((o) => o.id)))}
                        className="size-4 cursor-pointer accent-brand-700"
                      />
                    </th>
                    <th className="py-2.5 pr-5 pl-2">Oferta</th>
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
                      className={`cursor-pointer ${selected.has(offer.id) ? 'bg-brand-50/60 hover:bg-brand-50' : 'hover:bg-stone-50'}`}
                      onClick={open(`/ofertas/${offer.id}`)}
                    >
                      <td className="w-10 p-0">
                        {/* A célula inteira marca a oferta, sem abrir a tela dela. */}
                        <label className="flex h-full cursor-pointer items-center py-3 pr-2 pl-5">
                          <input
                            type="checkbox"
                            aria-label={`Selecionar ${offer.name}`}
                            checked={selected.has(offer.id)}
                            onChange={() => toggle(offer.id)}
                            className="size-4 cursor-pointer accent-brand-700"
                          />
                        </label>
                      </td>
                      <td className="py-3 pr-5 pl-2">
                        <Link to={`/ofertas/${offer.id}`} className="font-medium text-stone-900 hover:text-brand-800">
                          {offer.name}
                        </Link>
                        <span className="mt-1 flex flex-wrap items-center gap-1.5">
                          <OfferStatusBadge offer={offer} />
                          {offer.expiresSoon && offer.status !== 'expired' && (
                            <Badge tone="amber">{expiresIn(offer.validUntil, now)}</Badge>
                          )}
                          <span className="text-xs text-stone-500 tabular-nums">
                            {formatDuration(offer.durationMs)}
                          </span>
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
          </>
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
  // Sem loja marcada a oferta vale para todas (seção 6.3).
  if (offer.storeIds.length === 0) return 'Todas as lojas';
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

/**
 * Validade de várias ofertas juntas (D39, pedido do proprietário em 07/11/2026): marcar algumas ou todas e prorrogar
 * 15 ou 30 dias, ou pôr a mesma data de fim. Prorrogar conta do fim atual ou, na vencida, de hoje.
 */
function SelectionBar({
  offers,
  chosen,
  onSelect,
}: {
  offers: OfferSummary[];
  chosen: OfferSummary[];
  onSelect: (ids: number[]) => void;
}) {
  const change = useChangeOfferValidity();
  const toast = useToast();
  const [picking, setPicking] = useState(false);
  const [running, setRunning] = useState<string | null>(null);
  const expired = offers.filter((o) => o.status === 'expired');
  const count = chosen.length === 1 ? '1 oferta' : `${chosen.length} ofertas`;

  const apply = async (input: Omit<OfferValidityInput, 'offerIds'>, label: string, done: string) => {
    setRunning(label);
    try {
      await change.mutateAsync({ offerIds: chosen.map((o) => o.id), ...input });
      toast.success(done);
      onSelect([]);
      setPicking(false);
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setRunning(null);
    }
  };

  if (chosen.length === 0) {
    return (
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-stone-100 px-5 py-3 text-sm text-stone-500">
        <span>Marque as ofertas para prorrogar ou mudar a data de várias juntas.</span>
        {expired.length > 0 && (
          <Button size="sm" variant="secondary" onClick={() => onSelect(expired.map((o) => o.id))}>
            {expired.length === 1 ? 'Marcar a vencida' : `Marcar as ${expired.length} vencidas`}
          </Button>
        )}
      </div>
    );
  }

  return (
    <div className="border-b border-brand-100 bg-brand-50/60 px-5 py-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="mr-1 text-sm font-semibold text-stone-900">
          {chosen.length === 1 ? '1 selecionada' : `${chosen.length} selecionadas`}
        </span>
        {ExtendOptions.map((days) => (
          <Button
            key={days}
            size="sm"
            variant="primary"
            icon={<CalendarPlus className="size-4" />}
            busy={running === `+${days}`}
            disabled={running !== null}
            onClick={() =>
              void apply(
                { extendDays: days, validUntil: null },
                `+${days}`,
                `${chosen.length === 1 ? '1 oferta prorrogada' : `${chosen.length} ofertas prorrogadas`} por ${days} dias.`,
              )
            }
          >
            Prorrogar {days} dias
          </Button>
        ))}
        <Button
          size="sm"
          variant="secondary"
          icon={<CalendarClock className="size-4" />}
          disabled={running !== null}
          onClick={() => setPicking(true)}
        >
          Escolher a data
        </Button>
        <Button
          size="sm"
          variant="ghost"
          icon={<X className="size-4" />}
          disabled={running !== null}
          onClick={() => onSelect([])}
        >
          Limpar seleção
        </Button>
      </div>
      <p className="mt-1.5 text-xs text-stone-500">
        Prorrogar soma os dias no fim de cada oferta; a que já venceu volta a valer contando de hoje. O fim fica às
        23:59.
      </p>
      {picking && (
        <PickDateDialog
          chosen={chosen}
          busy={running === 'data'}
          onCancel={() => setPicking(false)}
          onApply={(until) =>
            void apply(
              { extendDays: null, validUntil: until },
              'data',
              `Fim de ${count} mudado para ${formatDateTime(until)}.`,
            )
          }
        />
      )}
    </div>
  );
}

/** Mesma data de fim para todas as ofertas marcadas. */
function PickDateDialog({
  chosen,
  busy,
  onCancel,
  onApply,
}: {
  chosen: OfferSummary[];
  busy: boolean;
  onCancel: () => void;
  onApply: (validUntil: string) => void;
}) {
  const [value, setValue] = useState(() => toLocalInput(endOfDayAfter(new Date(), DefaultValidityDays)));
  const until = fromLocalInput(value);
  const tooEarly = until === null ? [] : chosen.filter((o) => Date.parse(until) <= Date.parse(o.validFrom));

  return (
    <Modal
      title="Escolher o fim da validade"
      onClose={onCancel}
      footer={
        <>
          <Button variant="secondary" onClick={onCancel} disabled={busy}>
            Cancelar
          </Button>
          <Button
            variant="primary"
            busy={busy}
            disabled={until === null || tooEarly.length > 0}
            onClick={() => until && onApply(until)}
          >
            {chosen.length === 1 ? 'Aplicar na oferta' : `Aplicar nas ${chosen.length} ofertas`}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Termina em">
          <Input type="datetime-local" value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
        {tooEarly.length > 0 && (
          <ErrorNote>
            Essa data é antes do início de: {tooEarly.map((o) => o.name).join(', ')}. Escolha uma data depois.
          </ErrorNote>
        )}
        <ul className="max-h-48 space-y-1 overflow-y-auto text-sm text-stone-700">
          {chosen.map((offer) => (
            <li key={offer.id} className="flex items-center justify-between gap-3">
              <span className="truncate">{offer.name}</span>
              <span className="shrink-0 text-xs text-stone-500">atual: até {formatDateTime(offer.validUntil)}</span>
            </li>
          ))}
        </ul>
      </div>
    </Modal>
  );
}
