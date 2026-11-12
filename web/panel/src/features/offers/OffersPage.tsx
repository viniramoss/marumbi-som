import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import {
  CalendarClock,
  CalendarPlus,
  Clock,
  Folder as FolderIcon,
  FolderInput,
  FolderOpen,
  Megaphone,
  Plus,
  Upload,
  X,
} from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useChangeOfferStores, useChangeOfferValidity, useOffers, useStores } from '../../api/queries';
import type { OfferSummary, OfferValidityInput, StoreSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Badge, Button, Card, EmptyState, ErrorNote, Field, Input, PageHeader, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { useOpenOnClick } from '../../hooks/useOpenOnClick';
import { formatDateTime, formatDuration } from '../../lib/format';
import { DefaultValidityDays, endOfDayAfter, expiresIn, fromLocalInput, offerStatus, toLocalInput } from './offerDraft';
import { inFolder, isGeneral, parseFolder, storeNames, type Folder } from './offerFolders';

/** Botões de prorrogar da seleção (D39). */
const ExtendOptions = [15, 30] as const;

/** Lista de ofertas com a situação calculada pelo Hub e o aviso de vencimento (seção 7.2, D26 e). */
export function OffersPage() {
  const offers = useOffers();
  const stores = useStores();
  const navigate = useNavigate();
  const open = useOpenOnClick();
  const now = useNow(60_000);
  const [params, setParams] = useSearchParams();
  const storeList = stores.data ?? [];
  // Pastas por loja (D40): a pasta escolhida fica no endereço, para voltar a ela depois de abrir uma oferta.
  const folder = parseFolder(params.get('pasta'), storeList);
  const folderQuery = folder === 'todas' ? '' : `?pasta=${folder}`;
  const setFolder = (next: Folder) => {
    setSelected(new Set());
    setParams(next === 'todas' ? {} : { pasta: String(next) }, { replace: true });
  };
  const visible = (offers.data ?? []).filter((o) => inFolder(o, folder, storeList));
  const expiring = (offers.data ?? []).filter((o) => o.expiresSoon && o.status !== 'expired');
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  // Oferta removida em outra aba ou que saiu da pasta sai da seleção sozinha.
  const chosen = visible.filter((o) => selected.has(o.id));
  const allChosen = visible.length > 0 && chosen.length === visible.length;
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
          <>
            <Button icon={<Upload className="size-4" />} onClick={() => void navigate(`/ofertas/varias${folderQuery}`)}>
              Adicionar várias
            </Button>
            <Button
              variant="primary"
              icon={<Plus className="size-4" />}
              onClick={() => void navigate(`/ofertas/nova${folderQuery}`)}
            >
              Nova oferta
            </Button>
          </>
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
      {offers.data && offers.data.length > 0 && storeList.length > 1 && (
        <FolderBar offers={offers.data} stores={storeList} value={folder} onChange={setFolder} />
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
        ) : visible.length === 0 ? (
          <EmptyState icon={<FolderOpen className="size-10" />} title="Pasta vazia">
            {typeof folder === 'number'
              ? `Nenhuma oferta só de ${storeNames([folder], storeList)}. Use "Adicionar várias" ou "Nova oferta" daqui, ou marque ofertas em outra pasta e use "Mudar de pasta".`
              : 'Nenhuma oferta para todas as lojas.'}
          </EmptyState>
        ) : (
          <>
            <SelectionBar
              offers={visible}
              chosen={chosen}
              stores={storeList}
              onSelect={(ids) => setSelected(new Set(ids))}
            />
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
                        onChange={() => setSelected(new Set(allChosen ? [] : visible.map((o) => o.id)))}
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
                  {visible.map((offer) => (
                    <tr
                      key={offer.id}
                      className={`cursor-pointer ${selected.has(offer.id) ? 'bg-brand-50/60 hover:bg-brand-50' : 'hover:bg-stone-50'}`}
                      onClick={open(`/ofertas/${offer.id}${folderQuery}`)}
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
                        <Link
                          to={`/ofertas/${offer.id}${folderQuery}`}
                          className="font-medium text-stone-900 hover:text-brand-800"
                        >
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
  if (isGeneral(offer, stores ?? [])) return 'Todas as lojas';
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
  stores,
  onSelect,
}: {
  offers: OfferSummary[];
  chosen: OfferSummary[];
  stores: StoreSummary[];
  onSelect: (ids: number[]) => void;
}) {
  const change = useChangeOfferValidity();
  const move = useChangeOfferStores();
  const toast = useToast();
  const [picking, setPicking] = useState(false);
  const [moving, setMoving] = useState(false);
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
        <span>
          Marque as ofertas para prorrogar, mudar a data{stores.length > 1 ? ' ou mudar de pasta' : ''} de várias
          juntas.
        </span>
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
        {stores.length > 1 && (
          <Button
            size="sm"
            variant="secondary"
            icon={<FolderInput className="size-4" />}
            disabled={running !== null}
            onClick={() => setMoving(true)}
          >
            Mudar de pasta
          </Button>
        )}
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
      {moving && (
        <MoveDialog
          chosen={chosen}
          stores={stores}
          busy={running === 'pasta'}
          onCancel={() => setMoving(false)}
          onApply={(storeIds) => {
            setRunning('pasta');
            move
              .mutateAsync({ offerIds: chosen.map((o) => o.id), storeIds })
              .then(() => {
                toast.success(
                  `${count} ${chosen.length === 1 ? 'foi' : 'foram'} para ${storeIds.length === 0 ? 'a pasta Geral (todas as lojas)' : `a pasta ${storeNames(storeIds, stores)}`}.`,
                );
                onSelect([]);
                setMoving(false);
              })
              .catch((error: unknown) => toast.error(errorMessage(error)))
              .finally(() => setRunning(null));
          }}
        />
      )}
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

/**
 * Pastas por loja (D40): Todas, Geral (toca em todas as lojas) e uma pasta por loja, com quantas ofertas há em cada.
 * A oferta marcada para duas lojas aparece nas duas pastas.
 */
function FolderBar({
  offers,
  stores,
  value,
  onChange,
}: {
  offers: OfferSummary[];
  stores: StoreSummary[];
  value: Folder;
  onChange: (folder: Folder) => void;
}) {
  const folders: { id: Folder; label: string; hint: string }[] = [
    { id: 'todas', label: 'Todas', hint: 'todas as pastas' },
    { id: 'geral', label: 'Geral', hint: 'tocam em todas as lojas' },
    ...stores.map((store) => ({ id: store.id as Folder, label: store.name, hint: 'só nesta loja' })),
  ];
  return (
    <nav aria-label="Pastas de ofertas" className="mb-4 flex gap-2 overflow-x-auto pb-1">
      {folders.map((folder) => {
        const active = folder.id === value;
        const count = offers.filter((o) => inFolder(o, folder.id, stores)).length;
        const Icon = active ? FolderOpen : FolderIcon;
        return (
          <button
            key={String(folder.id)}
            type="button"
            aria-current={active ? 'page' : undefined}
            onClick={() => onChange(folder.id)}
            className={`flex min-w-36 shrink-0 items-center gap-2.5 rounded-xl px-3 py-2.5 text-left shadow-sm ring-1 transition-colors ${
              active ? 'bg-brand-50 ring-brand-200' : 'bg-white ring-stone-200 hover:bg-stone-50 hover:ring-stone-300'
            }`}
          >
            <Icon className={`size-5 shrink-0 ${active ? 'text-brand-700' : 'text-stone-400'}`} aria-hidden />
            <span className="min-w-0">
              <span className="flex items-center gap-1.5 text-sm font-semibold text-stone-900">
                <span className="truncate">{folder.label}</span>
                <span className="rounded-full bg-stone-100 px-1.5 text-xs font-medium text-stone-600 tabular-nums">
                  {count}
                </span>
              </span>
              <span className="block text-xs text-stone-500">{folder.hint}</span>
            </span>
          </button>
        );
      })}
    </nav>
  );
}

/** Pasta nova para as ofertas marcadas: Geral (todas as lojas) ou uma ou mais lojas. */
function MoveDialog({
  chosen,
  stores,
  busy,
  onCancel,
  onApply,
}: {
  chosen: OfferSummary[];
  stores: StoreSummary[];
  busy: boolean;
  onCancel: () => void;
  onApply: (storeIds: number[]) => void;
}) {
  const [storeIds, setStoreIds] = useState<number[]>([]);
  const toggle = (id: number) => {
    const next = storeIds.includes(id) ? storeIds.filter((s) => s !== id) : [...storeIds, id];
    setStoreIds(next.length === stores.length ? [] : next.sort((a, b) => a - b));
  };
  const option = (active: boolean) =>
    `flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-left text-sm ring-1 transition-colors ${
      active ? 'bg-brand-50 ring-brand-200' : 'bg-white ring-stone-200 hover:bg-stone-50'
    }`;
  return (
    <Modal
      title={chosen.length === 1 ? 'Mudar a oferta de pasta' : `Mudar ${chosen.length} ofertas de pasta`}
      onClose={onCancel}
      footer={
        <>
          <Button variant="secondary" onClick={onCancel} disabled={busy}>
            Cancelar
          </Button>
          <Button variant="primary" busy={busy} onClick={() => onApply(storeIds)}>
            {storeIds.length === 0 ? 'Mover para Geral' : `Mover para ${storeNames(storeIds, stores)}`}
          </Button>
        </>
      }
    >
      <div className="space-y-2">
        <p className="mb-3 text-sm text-stone-600">
          A pasta é a loja onde a oferta toca. Na playlist exclusiva de uma loja só aparecem as ofertas da pasta Geral e
          da pasta daquela loja.
        </p>
        <button
          type="button"
          aria-pressed={storeIds.length === 0}
          className={option(storeIds.length === 0)}
          onClick={() => setStoreIds([])}
        >
          <FolderIcon className="size-4 shrink-0 text-stone-400" aria-hidden />
          <span>
            <span className="block font-medium text-stone-900">Geral</span>
            <span className="block text-xs text-stone-500">
              Toca em todas as lojas, inclusive as cadastradas depois.
            </span>
          </span>
        </button>
        {stores.map((store) => (
          <button
            key={store.id}
            type="button"
            aria-pressed={storeIds.includes(store.id)}
            className={option(storeIds.includes(store.id))}
            onClick={() => toggle(store.id)}
          >
            <FolderIcon className="size-4 shrink-0 text-stone-400" aria-hidden />
            <span className="font-medium text-stone-900">{store.name}</span>
          </button>
        ))}
        <p className="pt-1 text-xs text-stone-500">Marque mais de uma loja para a oferta ficar nas duas pastas.</p>
      </div>
    </Modal>
  );
}
