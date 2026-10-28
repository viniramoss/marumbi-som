import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, CircleAlert, Clock, Pause, Play, Plus, Save, Timer, Trash2, Upload, X } from 'lucide-react';
import { errorMessage } from '../../api/client';
import {
  useCreateOffer,
  useDeleteOffer,
  useOffer,
  useOfferAudio,
  usePlaylists,
  useStores,
  useUpdateOffer,
} from '../../api/queries';
import type { IngestionJob, OfferDetail, StoreSummary } from '../../api/types';
import { acceptedAudio, isAudioFile, uploadTrack, waitForIngestion } from '../../api/upload';
import { AppliedStatus } from '../../components/AppliedStatus';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  EmptyState,
  ErrorNote,
  Field,
  IconButton,
  Input,
  ProgressBar,
  Spinner,
} from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { usePreview } from '../../hooks/usePreview';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { formatDuration, weekdayLetter, weekdayShort } from '../../lib/format';
import {
  draftFromOffer,
  draftProblem,
  expiresIn,
  gapWarning,
  isOfferDirty,
  newOfferDraft,
  newRule,
  ruleText,
  toOfferInput,
  type OfferDraft,
  type RuleDraft,
} from './offerDraft';
import { OfferStatusBadge } from './OffersPage';

/** Criar (/ofertas/nova) ou editar uma oferta (seção 7.2 e D26). */
export function OfferEditorPage() {
  const param = useParams().id;
  const isNew = param === 'nova';
  const offer = useOffer(Number(param), !isNew);
  const stores = useStores();

  if (isNew) {
    if (stores.isPending) return <Spinner />;
    return <Editor key="nova" saved={null} defaultStoreIds={(stores.data ?? []).map((s) => s.id)} />;
  }
  if (offer.isPending) return <Spinner />;
  if (offer.isError) return <ErrorNote>{errorMessage(offer.error)}</ErrorNote>;
  return <Editor key={offer.data.offer.id} saved={offer.data} defaultStoreIds={[]} />;
}

function Editor({ saved, defaultStoreIds }: { saved: OfferDetail | null; defaultStoreIds: number[] }) {
  const [initial] = useState(() => (saved ? draftFromOffer(saved) : newOfferDraft(new Date(), defaultStoreIds)));
  const [created, setCreated] = useState<{ id: number; draft: OfferDraft } | null>(null);
  const [draft, setDraft] = useState<OfferDraft>(initial);
  const [deleting, setDeleting] = useState(false);
  const stores = useStores();
  const baseline = useMemo(
    () => (saved ? draftFromOffer(saved) : (created?.draft ?? initial)),
    [saved, created, initial],
  );
  const dirty = isOfferDirty(draft, baseline);
  const create = useCreateOffer();
  const update = useUpdateOffer(saved?.offer.id ?? 0);
  const remove = useDeleteOffer();
  const offerAudio = useOfferAudio();
  const toast = useToast();
  const navigate = useNavigate();
  const now = useNow(60_000);

  const leaveDialog = useUnsavedChanges(
    dirty,
    'As alterações desta oferta ainda não foram salvas e não chegaram às lojas.',
  );

  // Depois de criar, vai para o endereço da oferta (só depois que o rascunho virou o salvo, para não perguntar).
  useEffect(() => {
    if (created) void navigate(`/ofertas/${created.id}`, { replace: true });
  }, [created, navigate]);

  const durationMs =
    draft.audioHash === null
      ? null
      : draft.audioHash === saved?.offer.audioHash
        ? saved.offer.durationMs
        : (offerAudio.data?.find((t) => t.hash === draft.audioHash)?.durationMs ?? null);

  const change = (patch: Partial<OfferDraft>) => setDraft((d) => ({ ...d, ...patch }));

  const publish = async () => {
    const problem = draftProblem(draft);
    if (problem) {
      toast.error(problem);
      return;
    }
    try {
      if (saved) {
        const detail = await update.mutateAsync(toOfferInput(draft));
        setDraft(draftFromOffer(detail));
      } else {
        const detail = await create.mutateAsync(toOfferInput(draft));
        const next = draftFromOffer(detail);
        setDraft(next);
        setCreated({ id: detail.offer.id, draft: next });
      }
      toast.success(draft.storeIds.length > 0 ? 'Oferta salva e publicada para as lojas escolhidas.' : 'Oferta salva.');
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  const confirmDelete = async () => {
    if (!saved) return;
    try {
      await remove.mutateAsync(saved.offer.id);
      setDeleting(false);
      toast.success('Oferta removida.');
      void navigate('/ofertas', { replace: true });
    } catch (error) {
      setDeleting(false);
      toast.error(errorMessage(error));
    }
  };

  return (
    <>
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <Link to="/ofertas" className="inline-flex items-center gap-1 text-sm text-stone-500 hover:text-stone-800">
          <ArrowLeft className="size-4" /> Ofertas
        </Link>
      </div>

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0 flex-1">
          <label htmlFor="offer-name" className="sr-only">
            Nome da oferta
          </label>
          <input
            id="offer-name"
            value={draft.name}
            maxLength={80}
            placeholder={'Nome da oferta, ex.: "Picanha R$ 69,90"'}
            onChange={(e) => change({ name: e.target.value })}
            className="w-full max-w-xl rounded-lg border-0 bg-transparent px-2 py-1 text-2xl font-semibold tracking-tight ring-0 placeholder:text-stone-300 hover:bg-white focus:bg-white focus:ring-2 focus:ring-brand-600 focus:outline-none"
          />
          <p className="mt-1 flex flex-wrap items-center gap-2 px-2 text-sm text-stone-500">
            {saved ? <OfferStatusBadge offer={saved.offer} /> : <span>Oferta nova</span>}
            {saved?.offer.expiresSoon && saved.offer.status !== 'expired' && (
              <Badge tone="amber">{expiresIn(saved.offer.validUntil, now)}</Badge>
            )}
            {dirty && <span className="font-medium text-amber-700">alterações não salvas</span>}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {saved && (
            <Button icon={<Trash2 className="size-4" />} variant="danger" onClick={() => setDeleting(true)}>
              Remover
            </Button>
          )}
          <Button
            variant="primary"
            icon={<Save className="size-4" />}
            busy={create.isPending || update.isPending}
            disabled={!dirty}
            onClick={() => void publish()}
          >
            Salvar e publicar
          </Button>
        </div>
      </div>

      <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="space-y-6">
          <AudioCard
            hash={draft.audioHash}
            durationMs={durationMs}
            onUploaded={(hash, fileName) =>
              setDraft((d) => ({ ...d, audioHash: hash, name: d.name.trim() ? d.name : nameFromFile(fileName) }))
            }
          />
          <RulesCard
            rules={draft.rules}
            hasAnchors={(saved?.anchors.length ?? 0) > 0}
            stores={(stores.data ?? []).filter((s) => draft.storeIds.includes(s.id))}
            onChange={(rules) => change({ rules })}
          />
          <AnchorsCard saved={saved} />
        </div>
        <div className="space-y-6">
          <Card>
            <CardHeader title="Validade" />
            <div className="space-y-4 px-5 py-4">
              <Field label="Começa">
                <Input
                  type="datetime-local"
                  value={draft.validFrom}
                  onChange={(e) => change({ validFrom: e.target.value })}
                />
              </Field>
              <Field label="Termina" hint="Depois disso a oferta para de tocar sozinha, sem precisar apagar.">
                <Input
                  type="datetime-local"
                  value={draft.validUntil}
                  onChange={(e) => change({ validUntil: e.target.value })}
                />
              </Field>
              <label className="flex cursor-pointer items-start gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={draft.enabled}
                  onChange={(e) => change({ enabled: e.target.checked })}
                  className="mt-0.5 size-4 accent-brand-700"
                />
                <span>
                  <span className="font-medium text-stone-800">Ativa</span>
                  <span className="block text-xs text-stone-500">
                    Desativada, a oferta não toca em nenhuma loja, mesmo dentro da validade.
                  </span>
                </span>
              </label>
            </div>
          </Card>
          <StoresCard
            storeIds={draft.storeIds}
            savedStoreIds={saved?.offer.storeIds ?? []}
            onChange={(storeIds) => change({ storeIds })}
          />
          <DuckCard value={draft.duckLevel} onChange={(duckLevel) => change({ duckLevel })} />
          {saved && <PlaysCard saved={saved} />}
        </div>
      </div>

      {deleting && saved && (
        <ConfirmDialog
          title="Remover a oferta?"
          confirmLabel="Remover"
          busy={remove.isPending}
          onCancel={() => setDeleting(false)}
          onConfirm={() => void confirmDelete()}
        >
          <p>
            A oferta <strong>{saved.offer.name}</strong> será apagada, com as regras e as músicas onde está presa. As
            lojas param de tocá-la assim que receberem a configuração nova.
          </p>
          <p>O histórico do que já tocou continua guardado.</p>
        </ConfirmDialog>
      )}
      {leaveDialog}
    </>
  );
}

/** "picanha-69,90.mp3" → "picanha-69,90". */
function nameFromFile(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return (dot > 0 ? fileName.slice(0, dot) : fileName).slice(0, 80);
}

interface UploadState {
  name: string;
  progress: number;
  job?: IngestionJob;
  error?: string;
}

/** Áudio da oferta: envio com barra de andamento, processamento no Hub e pré-escuta (D26 a). */
function AudioCard({
  hash,
  durationMs,
  onUploaded,
}: {
  hash: string | null;
  durationMs: number | null;
  onUploaded: (hash: string, fileName: string) => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [upload, setUpload] = useState<UploadState | null>(null);
  const [dragging, setDragging] = useState(false);
  const preview = usePreview();
  const busy = upload !== null && !upload.error;

  const start = async (file: File) => {
    if (!isAudioFile(file.name)) {
      setUpload({ name: file.name, progress: 0, error: 'Formato não aceito.' });
      return;
    }
    setUpload({ name: file.name, progress: 0 });
    try {
      const [job] = await uploadTrack(file, (progress) => setUpload((u) => u && { ...u, progress }), 'offer');
      if (!job || job.status === 'rejected') throw new Error(job?.error ?? 'O Hub recusou o arquivo.');
      const done = await waitForIngestion(job.id, (current) =>
        setUpload((u) => u && { ...u, progress: 1, job: current }),
      );
      if ((done.status === 'done' || done.status === 'duplicate') && done.hash) {
        setUpload(null);
        onUploaded(done.hash, file.name);
      } else {
        throw new Error(done.error ?? 'O Hub não conseguiu processar o áudio.');
      }
    } catch (error) {
      setUpload((u) => u && { ...u, error: errorMessage(error) });
    }
  };

  const choose = (files: FileList | File[] | null) => {
    const file = files?.[0];
    if (file && !busy) void start(file);
  };

  return (
    <Card>
      <CardHeader
        title="Áudio"
        subtitle="O Hub corta o silêncio e iguala o volume da locução (−14 LUFS). Um arquivo por oferta."
      />
      <div className="space-y-4 px-5 py-4">
        {hash && !busy && (
          <div className="flex flex-wrap items-center gap-3 rounded-lg bg-stone-50 px-3 py-2.5 ring-1 ring-stone-200">
            <IconButton
              label={preview.playing === hash ? 'Parar pré-escuta' : 'Ouvir a oferta'}
              onClick={() => preview.toggle(hash)}
            >
              {preview.playing === hash ? <Pause className="size-4 text-brand-700" /> : <Play className="size-4" />}
            </IconButton>
            <span className="min-w-0 flex-1 text-sm">
              <span className="block font-medium text-stone-900">Áudio pronto</span>
              <span className="block text-xs text-stone-500 tabular-nums">Duração {formatDuration(durationMs)}</span>
            </span>
            <Button size="sm" icon={<Upload className="size-4" />} onClick={() => input.current?.click()}>
              Trocar áudio
            </Button>
          </div>
        )}
        {!hash && !busy && (
          <div
            onDragOver={(event) => {
              event.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(event) => {
              event.preventDefault();
              setDragging(false);
              choose(Array.from(event.dataTransfer.files));
            }}
            className={`flex flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed px-6 py-8 text-center transition-colors ${
              dragging ? 'border-brand-600 bg-brand-50' : 'border-stone-300 bg-stone-50'
            }`}
          >
            <Upload className="size-8 text-stone-400" aria-hidden />
            <p className="text-sm text-stone-600">Arraste o áudio gravado para cá</p>
            <Button variant="primary" onClick={() => input.current?.click()} icon={<Upload className="size-4" />}>
              Escolher arquivo
            </Button>
            <p className="text-xs text-stone-500">{acceptedAudio.join(' ')}</p>
          </div>
        )}
        {upload && <UploadProgress upload={upload} onRetry={() => input.current?.click()} />}
        <input
          ref={input}
          type="file"
          accept={acceptedAudio.join(',')}
          className="hidden"
          data-testid="offer-audio-input"
          onChange={(event) => {
            const files = Array.from(event.target.files ?? []);
            event.target.value = '';
            choose(files);
          }}
        />
      </div>
    </Card>
  );
}

function UploadProgress({ upload, onRetry }: { upload: UploadState; onRetry: () => void }) {
  if (upload.error) {
    return (
      <div className="flex flex-wrap items-center gap-3">
        <ErrorNote>
          {upload.name}: {upload.error}
        </ErrorNote>
        <Button size="sm" onClick={onRetry}>
          Escolher outro arquivo
        </Button>
      </div>
    );
  }
  const processing = upload.progress >= 1;
  return (
    <div className="space-y-1.5" aria-live="polite">
      <div className="flex justify-between gap-3 text-sm">
        <span className="truncate text-stone-800">{upload.name}</span>
        <span className="shrink-0 text-xs text-stone-500 tabular-nums">
          {!processing
            ? `Enviando ${Math.round(upload.progress * 100)}%`
            : upload.job?.status === 'processing'
              ? 'Processando (silêncio, volume)…'
              : 'Na fila do Hub…'}
        </span>
      </div>
      {processing ? (
        <ProgressBar label={`Processando ${upload.name}`} />
      ) : (
        <ProgressBar value={upload.progress * 100} label={`Enviando ${upload.name}`} />
      )}
    </div>
  );
}

/** Regras de recorrência: só "a cada X minutos" e "horário fixo", sempre durante a música (D24, D26 b). */
function RulesCard({
  rules,
  hasAnchors,
  stores,
  onChange,
}: {
  rules: RuleDraft[];
  hasAnchors: boolean;
  /** Lojas escolhidas para a oferta, para o aviso do intervalo mínimo entre ofertas. */
  stores: StoreSummary[];
  onChange: (rules: RuleDraft[]) => void;
}) {
  const playlists = usePlaylists();
  const update = (key: string, patch: Partial<RuleDraft>) =>
    onChange(rules.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const remove = (key: string) => onChange(rules.filter((r) => r.key !== key));
  const playlistName = (id: number) => playlists.data?.find((p) => p.id === id)?.name;

  return (
    <Card>
      <CardHeader
        title="Quando toca"
        subtitle="Sempre por cima da música, com a música abaixada. O intervalo mínimo entre ofertas de cada loja vale para todas as regras."
      />
      <div className="space-y-3 px-5 py-4">
        {rules.length === 0 && (
          <p className="rounded-lg bg-stone-50 px-3 py-2 text-sm text-stone-600 ring-1 ring-stone-200">
            {hasAnchors
              ? 'Sem regra de tempo: a oferta toca nas músicas onde está presa e pelo botão "tocar oferta agora".'
              : 'Sem regra a oferta só toca presa a uma música da playlist ou pelo botão "tocar oferta agora" da loja.'}
          </p>
        )}
        {rules.map((rule) =>
          rule.original ? (
            <div
              key={rule.key}
              className="flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm ring-1 ring-stone-200"
            >
              <span className="min-w-0 flex-1">
                <span className="block text-stone-800">{ruleText(rule.original, playlistName)}</span>
                <span className="block text-xs text-stone-500">Criada fora do painel. Pode ser removida aqui.</span>
              </span>
              <IconButton label="Remover regra" onClick={() => remove(rule.key)}>
                <X className="size-4" />
              </IconButton>
            </div>
          ) : (
            <RuleEditor
              key={rule.key}
              rule={rule}
              playlists={playlists.data ?? []}
              stores={stores}
              onChange={(patch) => update(rule.key, patch)}
              onRemove={() => remove(rule.key)}
            />
          ),
        )}
        <div className="flex flex-wrap gap-2 pt-1">
          <Button
            size="sm"
            icon={<Plus className="size-4" />}
            onClick={() => onChange([...rules, newRule('everyMinutes')])}
          >
            A cada X minutos
          </Button>
          <Button size="sm" icon={<Plus className="size-4" />} onClick={() => onChange([...rules, newRule('clock')])}>
            Horário fixo
          </Button>
        </div>
      </div>
    </Card>
  );
}

function RuleEditor({
  rule,
  playlists,
  stores,
  onChange,
  onRemove,
}: {
  rule: RuleDraft;
  playlists: { id: number; name: string }[];
  stores: StoreSummary[];
  onChange: (patch: Partial<RuleDraft>) => void;
  onRemove: () => void;
}) {
  const toggleDay = (day: number) =>
    onChange({ days: rule.days.includes(day) ? rule.days.filter((d) => d !== day) : [...rule.days, day] });
  const togglePlaylist = (id: number) =>
    onChange({
      playlistIds: rule.playlistIds.includes(id) ? rule.playlistIds.filter((p) => p !== id) : [...rule.playlistIds, id],
    });

  return (
    <div className="space-y-3 rounded-lg px-3 py-3 ring-1 ring-stone-200">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
        {rule.kind === 'everyMinutes' ? (
          <label className="inline-flex items-center gap-2 whitespace-nowrap">
            <Timer className="size-4 shrink-0 text-stone-400" aria-hidden />A cada
            <span className="w-20 shrink-0">
              <Input
                type="number"
                min={1}
                max={1440}
                value={Number.isFinite(rule.minutes) ? rule.minutes : ''}
                onChange={(e) => onChange({ minutes: e.target.valueAsNumber })}
                aria-label="Minutos entre uma vez e outra"
                className="text-right"
              />
            </span>
            minutos
          </label>
        ) : (
          <>
            <label className="inline-flex items-center gap-2 whitespace-nowrap">
              <Clock className="size-4 shrink-0 text-stone-400" aria-hidden />
              Às
              <span className="w-32 shrink-0">
                <Input
                  type="time"
                  value={rule.time}
                  onChange={(e) => onChange({ time: e.target.value })}
                  aria-label="Hora"
                />
              </span>
            </label>
            <span className="inline-flex gap-1" role="group" aria-label="Dias da semana">
              {weekdayLetter.map((letter, day) => (
                <button
                  key={day}
                  type="button"
                  aria-pressed={rule.days.includes(day)}
                  aria-label={weekdayShort[day]}
                  title={weekdayShort[day]}
                  onClick={() => toggleDay(day)}
                  className={`size-8 rounded-full text-xs font-semibold ring-1 transition-colors ${
                    rule.days.includes(day)
                      ? 'bg-brand-700 text-white ring-brand-700'
                      : 'bg-white text-stone-600 ring-stone-300 hover:bg-stone-50'
                  }`}
                >
                  {letter}
                </button>
              ))}
            </span>
          </>
        )}
        <IconButton label="Remover regra" onClick={onRemove} className="ml-auto">
          <X className="size-4" />
        </IconButton>
      </div>
      {rule.kind === 'everyMinutes' && gapWarning(rule.minutes, stores) && (
        <p className="flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 ring-1 ring-amber-200">
          <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
          <span>{gapWarning(rule.minutes, stores)}</span>
        </p>
      )}
      {playlists.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 text-xs">
          <span className="text-stone-500">
            {rule.playlistIds.length === 0 ? 'Em todas as playlists. Só em:' : 'Só nas playlists:'}
          </span>
          {playlists.map((playlist) => (
            <button
              key={playlist.id}
              type="button"
              aria-pressed={rule.playlistIds.includes(playlist.id)}
              onClick={() => togglePlaylist(playlist.id)}
              className={`rounded-full px-2.5 py-1 font-medium ring-1 transition-colors ${
                rule.playlistIds.includes(playlist.id)
                  ? 'bg-brand-50 text-brand-800 ring-brand-300'
                  : 'bg-white text-stone-600 ring-stone-200 hover:bg-stone-50'
              }`}
            >
              {playlist.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Onde a oferta está presa (somente leitura; edição no editor de playlist, seção 7.2). */
function AnchorsCard({ saved }: { saved: OfferDetail | null }) {
  return (
    <Card>
      <CardHeader title="Presa em músicas" subtitle="Para prender ou soltar, abra a playlist." />
      {!saved ? (
        <p className="px-5 py-4 text-sm text-stone-500">
          Salve a oferta para poder prendê-la a uma música, no editor da playlist.
        </p>
      ) : saved.anchors.length === 0 ? (
        <p className="px-5 py-4 text-sm text-stone-500">
          Em nenhuma música. No editor da playlist, arraste a oferta para cima de uma música e escolha o momento em que
          ela entra.
        </p>
      ) : (
        <ul className="divide-y divide-stone-100">
          {saved.anchors.map((anchor) => (
            <li key={anchor.id} className="flex items-center gap-3 px-5 py-2.5 text-sm">
              <span className="min-w-0 flex-1">
                <Link
                  to={`/playlists/${anchor.playlistId}`}
                  className="block truncate font-medium text-stone-900 hover:text-brand-800"
                >
                  {anchor.playlistName}
                </Link>
                <span className="block truncate text-xs text-stone-500">
                  {anchor.position}. {anchor.trackTitle}
                </span>
              </span>
              <span className="shrink-0 text-stone-600 tabular-nums">
                {anchor.offsetMs === null ? 'entre músicas' : `aos ${formatDuration(anchor.offsetMs)}`}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

function StoresCard({
  storeIds,
  savedStoreIds,
  onChange,
}: {
  storeIds: number[];
  savedStoreIds: number[];
  onChange: (ids: number[]) => void;
}) {
  const stores = useStores();
  const all = (stores.data ?? []).map((s) => s.id);
  const allChosen = all.length > 0 && all.every((id) => storeIds.includes(id));
  const toggle = (id: number) => onChange(storeIds.includes(id) ? storeIds.filter((s) => s !== id) : [...storeIds, id]);

  return (
    <Card>
      <CardHeader
        title="Lojas"
        actions={
          all.length > 1 && (
            <Button size="sm" variant="ghost" onClick={() => onChange(allChosen ? [] : all)}>
              {allChosen ? 'Nenhuma' : 'Todas'}
            </Button>
          )
        }
      />
      <div className="space-y-4 px-5 py-4">
        {stores.isPending ? (
          <Spinner />
        ) : all.length === 0 ? (
          <EmptyState title="Nenhuma loja cadastrada" />
        ) : (
          <ul className="space-y-2">
            {stores.data!.map((store) => (
              <li key={store.id}>
                <label className="flex cursor-pointer items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={storeIds.includes(store.id)}
                    onChange={() => toggle(store.id)}
                    className="size-4 accent-brand-700"
                  />
                  {store.name}
                </label>
              </li>
            ))}
          </ul>
        )}
        {storeIds.length === 0 && all.length > 0 && (
          <p className="flex items-start gap-1.5 text-xs text-amber-800">
            <CircleAlert className="mt-px size-3.5 shrink-0" aria-hidden />
            Sem loja escolhida a oferta não toca em lugar nenhum.
          </p>
        )}
        {savedStoreIds.length > 0 && (
          <div className="border-t border-stone-100 pt-3">
            <p className="mb-2 text-xs font-medium text-stone-500">Situação depois do último salvamento</p>
            <AppliedStatus storeIds={savedStoreIds} emptyText="Nenhuma loja." />
          </div>
        )}
      </div>
    </Card>
  );
}

/** Nível do ducking: quanto a música fica durante a oferta (seção 10.3.3, padrão 30%). */
function DuckCard({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  return (
    <Card>
      <CardHeader title="Música durante a oferta" />
      <div className="space-y-2 px-5 py-4">
        <div className="flex items-center gap-3">
          <input
            type="range"
            min={10}
            max={90}
            step={5}
            value={value}
            aria-label="Volume da música durante a oferta"
            onChange={(e) => onChange(Number(e.target.value))}
            className="h-2 w-full cursor-pointer accent-brand-700"
          />
          <span className="w-12 text-right text-sm font-semibold tabular-nums">{value}%</span>
        </div>
        <p className="text-xs text-stone-500">
          A música abaixa para {value}% enquanto a oferta fala e volta sozinha depois. Padrão 30%. A música nunca para.
        </p>
      </div>
    </Card>
  );
}

function PlaysCard({ saved }: { saved: OfferDetail }) {
  const rows = saved.plays.filter((p) => p.played + p.interrupted + p.skipped > 0);
  return (
    <Card>
      <CardHeader title="Execuções nos últimos 7 dias" />
      {rows.length === 0 ? (
        <p className="px-5 py-4 text-sm text-stone-500">Ainda não tocou nos últimos 7 dias.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-stone-100 text-left text-xs font-medium text-stone-500">
              <th className="px-5 py-2">Loja</th>
              <Th title="Tocou até o fim">Tocou</Th>
              <Th title="Começou e foi cortada (pular, pausar, caixa desconectada)">Cortada</Th>
              <Th title="Não chegou a tocar (fila cheia, fora do horário, espera passou de 10 min)">Descartada</Th>
            </tr>
          </thead>
          <tbody className="divide-y divide-stone-100">
            {rows.map((row) => (
              <tr key={row.storeId}>
                <td className="px-5 py-2 text-stone-800">{row.storeName}</td>
                <td className="py-2 pr-5 text-right tabular-nums">{row.played}</td>
                <td className="py-2 pr-5 text-right tabular-nums">{row.interrupted}</td>
                <td className="py-2 pr-5 text-right tabular-nums">{row.skipped}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function Th({ title, children }: { title: string; children: ReactNode }) {
  return (
    <th className="py-2 pr-5 text-right" title={title}>
      {children}
    </th>
  );
}
