import { useState } from 'react';
import type { OfferSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { Button, Field, Input, Select } from '../../components/ui';
import { formatDuration } from '../../lib/format';
import { maxAnchorOffset, parseClock, type DraftItem } from './playlistDraft';

export interface AnchorChoice {
  key?: string;
  offerId: number;
  offerName: string;
  itemKey: string;
  offsetMs: number;
}

/**
 * Prender uma oferta a uma música (âncora "durante", D24): a oferta entra no tempo marcado, por cima da música. O tempo
 * vai de 0:00 até 5 s antes do fim da música.
 */
export function AnchorDialog({
  offers,
  items,
  initial,
  onSave,
  onRemove,
  onClose,
}: {
  offers: OfferSummary[];
  items: DraftItem[];
  initial: Partial<AnchorChoice> & { itemKey: string };
  onSave: (anchor: AnchorChoice) => void;
  onRemove?: () => void;
  onClose: () => void;
}) {
  const choices = offers.filter((o) => o.status !== 'expired' || o.id === initial.offerId);
  const [offerId, setOfferId] = useState(initial.offerId ?? choices[0]?.id ?? 0);
  const [itemKey, setItemKey] = useState(initial.itemKey);
  const item = items.find((i) => i.key === itemKey);
  const max = maxAnchorOffset(item?.durationMs ?? 0);
  const [offsetMs, setOffsetMs] = useState(Math.min(initial.offsetMs ?? 30_000, max));
  const [text, setText] = useState(formatDuration(Math.min(initial.offsetMs ?? 30_000, max)));
  const offer = offers.find((o) => o.id === offerId);
  const clamped = Math.min(offsetMs, max);
  const typed = parseClock(text);
  const invalid = typed === null || typed > max;

  const setOffset = (ms: number) => {
    setOffsetMs(ms);
    setText(formatDuration(ms));
  };

  const save = () => {
    if (!offer || !item || invalid) return;
    onSave({ key: initial.key, offerId: offer.id, offerName: offer.name, itemKey: item.key, offsetMs: clamped });
  };

  return (
    <Modal
      title={initial.key ? 'Oferta presa na música' : 'Prender oferta na música'}
      onClose={onClose}
      footer={
        <>
          {onRemove && (
            <Button variant="danger" onClick={onRemove} className="mr-auto">
              Soltar
            </Button>
          )}
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" disabled={!offer || !item || invalid} onClick={save}>
            {initial.key ? 'Aplicar' : 'Prender'}
          </Button>
        </>
      }
    >
      <form
        className="space-y-4"
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        {choices.length === 0 ? (
          <p className="text-sm text-stone-600">Nenhuma oferta cadastrada. Crie uma na tela de Ofertas.</p>
        ) : (
          <Field label="Oferta">
            <Select value={offerId} onChange={(e) => setOfferId(Number(e.target.value))}>
              {choices.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name} ({formatDuration(o.durationMs)})
                </option>
              ))}
            </Select>
          </Field>
        )}
        <Field label="Música">
          <Select
            value={itemKey}
            onChange={(e) => {
              setItemKey(e.target.value);
              const next = items.find((i) => i.key === e.target.value);
              if (next && offsetMs > maxAnchorOffset(next.durationMs)) setOffset(maxAnchorOffset(next.durationMs));
            }}
          >
            {items.map((i, index) => (
              <option key={i.key} value={i.key}>
                {index + 1}. {i.title} ({formatDuration(i.durationMs)})
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Entra aos" hint={`De 0:00 até ${formatDuration(max)} (5 s antes do fim da música).`}>
          <div className="flex items-center gap-3">
            <Input
              value={text}
              inputMode="numeric"
              aria-label="Momento em que a oferta entra (minutos:segundos)"
              onChange={(e) => {
                setText(e.target.value);
                const ms = parseClock(e.target.value);
                if (ms !== null && ms <= max) setOffsetMs(ms);
              }}
              className={`w-24 text-right tabular-nums ${invalid ? 'ring-red-400' : ''}`}
            />
            <input
              type="range"
              min={0}
              max={Math.max(0, Math.floor(max / 1000))}
              step={1}
              value={Math.floor(clamped / 1000)}
              aria-label="Momento em que a oferta entra"
              onChange={(e) => setOffset(Number(e.target.value) * 1000)}
              className="h-2 w-full cursor-pointer accent-brand-700"
            />
          </div>
        </Field>
        {invalid && <p className="text-sm text-red-700">Use minutos:segundos, de 0:00 até {formatDuration(max)}.</p>}
        {offer && item && !invalid && (
          <p className="rounded-lg bg-stone-50 px-3 py-2 text-sm text-stone-600 ring-1 ring-stone-200">
            A música abaixa e a oferta ({formatDuration(offer.durationMs)}) entra aos {formatDuration(clamped)} de "
            {item.title}".
            {clamped + offer.durationMs > item.durationMs &&
              ' Ela passa do fim da música; a próxima começa abaixada e sobe quando a oferta terminar.'}
          </p>
        )}
      </form>
    </Modal>
  );
}
