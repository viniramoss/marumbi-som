import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { ArrowLeft, CircleAlert, CircleCheck, Pause, Play, Plus, Upload, X } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { errorMessage } from '../../api/client';
import { useCreateOffers, useOfferAudio, useStores } from '../../api/queries';
import type { IngestionJob } from '../../api/types';
import { acceptedAudio, isAudioFile, uploadTrack, waitForIngestion } from '../../api/upload';
import { useToast } from '../../components/Toasts';
import {
  Button,
  Card,
  CardHeader,
  ErrorNote,
  Field,
  IconButton,
  Input,
  ProgressBar,
  Spinner,
} from '../../components/ui';
import { usePreview } from '../../hooks/usePreview';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { formatDuration } from '../../lib/format';
import { DuckCard, RulesCard, StoresCard } from './OfferEditorPage';
import { draftProblem, nameFromFile, newOfferDraft, toOfferInput, type OfferDraft } from './offerDraft';
import { isGeneral, parseFolder, storeIdsForFolder, storeNames } from './offerFolders';

/** Até quantos arquivos de uma vez (o Hub aceita até 50 ofertas por pedido). */
const MaxFiles = 50;

/** Um áudio da lista: espera, envia (com %), processa no Hub, fica pronto ou dá erro com o motivo. */
interface Row {
  key: string;
  file: File;
  name: string;
  status: 'waiting' | 'sending' | 'processing' | 'ready' | 'error';
  progress: number;
  job?: IngestionJob;
  hash?: string;
  error?: string;
}

let counter = 0;

/**
 * Várias ofertas de uma vez (D40 a): arrastar vários áudios, cada um vira uma oferta com o nome do arquivo (dá para
 * mudar), e a pasta, a validade, as regras e o nível da música valem para todas. Cada uma pode ser ajustada depois.
 */
export function OffersBatchPage() {
  const stores = useStores();
  if (stores.isPending) return <Spinner />;
  return <Batch />;
}

function Batch() {
  const stores = useStores();
  const all = stores.data ?? [];
  const [params] = useSearchParams();
  const pasta = params.get('pasta');
  const back = pasta ? `/ofertas?pasta=${encodeURIComponent(pasta)}` : '/ofertas';
  const [shared, setShared] = useState<OfferDraft>(() =>
    newOfferDraft(new Date(), storeIdsForFolder(parseFolder(pasta, all), all)),
  );
  const [rows, setRows] = useState<Row[]>([]);
  const [dragging, setDragging] = useState(false);
  const [done, setDone] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const create = useCreateOffers();
  const offerAudio = useOfferAudio();
  const client = useQueryClient();
  const preview = usePreview();
  const toast = useToast();
  const navigate = useNavigate();

  const update = (key: string, patch: Partial<Row>) =>
    setRows((list) => list.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const change = (patch: Partial<OfferDraft>) => setShared((d) => ({ ...d, ...patch }));

  const leaveDialog = useUnsavedChanges(
    rows.length > 0 && !done,
    'Os áudios enviados ainda não viraram ofertas. Saindo agora, nenhuma oferta é criada.',
  );

  // Um envio por vez (a barra de cada arquivo anda de verdade); o processamento no Hub corre em paralelo.
  const sending = rows.some((r) => r.status === 'sending');
  const next = rows.find((r) => r.status === 'waiting');
  const started = useRef(new Set<string>());
  useEffect(() => {
    if (sending || !next || started.current.has(next.key)) return;
    const { key, file } = next;
    started.current.add(key);
    update(key, { status: 'sending', progress: 0 });
    void (async () => {
      try {
        const [job] = await uploadTrack(file, (progress) => update(key, { progress }), 'offer');
        if (!job || job.status === 'rejected') throw new Error(job?.error ?? 'O Hub recusou o arquivo.');
        update(key, { status: 'processing', progress: 1, job });
        const result = await waitForIngestion(job.id, (current) => update(key, { job: current }));
        if ((result.status === 'done' || result.status === 'duplicate') && result.hash) {
          update(key, { status: 'ready', hash: result.hash });
          void client.invalidateQueries({ queryKey: ['offerAudio'] });
        } else {
          throw new Error(result.error ?? 'O Hub não conseguiu processar o áudio.');
        }
      } catch (error) {
        update(key, { status: 'error', error: errorMessage(error) });
      }
    })();
  }, [sending, next, client]);

  const add = (files: File[]) => {
    const room = MaxFiles - rows.length;
    if (files.length > room)
      toast.error(`No máximo ${MaxFiles} ofertas de uma vez; ${files.length - room} ficaram de fora.`);
    const added = files.slice(0, Math.max(0, room)).map((file): Row => ({
      key: `audio-${++counter}`,
      file,
      name: nameFromFile(file.name),
      ...(isAudioFile(file.name)
        ? { status: 'waiting', progress: 0 }
        : { status: 'error', progress: 0, error: `Formato não aceito (${acceptedAudio.join(' ')}).` }),
    }));
    setRows((list) => [...list, ...added]);
  };

  const ready = rows.filter((r) => r.status === 'ready');
  const busy = rows.some((r) => r.status === 'waiting' || r.status === 'sending' || r.status === 'processing');
  const failed = rows.filter((r) => r.status === 'error').length;
  const durationOf = (hash: string | undefined) => offerAudio.data?.find((t) => t.hash === hash)?.durationMs ?? null;
  const general = isGeneral(shared, all);
  const chosenStores = all.filter((s) => shared.storeIds.length === 0 || shared.storeIds.includes(s.id));

  const publish = async () => {
    const drafts = ready.map((row) => ({ ...shared, name: row.name, audioHash: row.hash! }));
    for (const draft of drafts) {
      const problem = draftProblem(draft);
      if (problem) {
        toast.error(draft.name.trim() ? `${draft.name}: ${problem}` : problem);
        return;
      }
    }
    try {
      const created = await create.mutateAsync(drafts.map(toOfferInput));
      setDone(true);
      const where = general ? 'todas as lojas' : storeNames(shared.storeIds, all);
      toast.success(
        `${created.length === 1 ? '1 oferta criada' : `${created.length} ofertas criadas`} e publicada${created.length === 1 ? '' : 's'} para ${where}.`,
      );
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  // Depois de criar, volta para a lista (na pasta das ofertas novas), sem perguntar se quer sair.
  useEffect(() => {
    if (!done) return;
    // Com uma loja só não há pastas.
    const target =
      all.length < 2
        ? '/ofertas'
        : general
          ? '/ofertas?pasta=geral'
          : shared.storeIds.length === 1
            ? `/ofertas?pasta=${shared.storeIds[0]!}`
            : '/ofertas';
    void navigate(target, { replace: true });
  }, [done, navigate, shared.storeIds, general, all.length]);

  return (
    <>
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <Link to={back} className="inline-flex items-center gap-1 text-sm text-stone-500 hover:text-stone-800">
          <ArrowLeft className="size-4" /> Ofertas
        </Link>
      </div>

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0 flex-1">
          <h1 className="text-2xl font-semibold tracking-tight text-stone-900">Adicionar várias ofertas</h1>
          <p className="mt-1 text-sm text-stone-500">
            Cada áudio vira uma oferta com o nome do arquivo. A pasta, a validade e quando tocam valem para todas;
            depois dá para ajustar cada uma.
          </p>
        </div>
        <Button
          variant="primary"
          icon={<CircleCheck className="size-4" />}
          busy={create.isPending}
          disabled={ready.length === 0 || busy || done}
          onClick={() => void publish()}
        >
          {busy ? 'Esperando os áudios…' : ready.length === 1 ? 'Criar 1 oferta' : `Criar ${ready.length} ofertas`}
        </Button>
      </div>

      <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="space-y-6">
          <Card>
            <CardHeader
              title="Áudios"
              subtitle="O Hub corta o silêncio e iguala o volume de cada locução (−14 LUFS)."
              actions={
                rows.length > 0 && (
                  <Button size="sm" icon={<Plus className="size-4" />} onClick={() => input.current?.click()}>
                    Mais áudios
                  </Button>
                )
              }
            />
            <div
              className="space-y-3 px-5 py-4"
              onDragOver={(event) => {
                event.preventDefault();
                setDragging(true);
              }}
              onDragLeave={() => setDragging(false)}
              onDrop={(event) => {
                event.preventDefault();
                setDragging(false);
                add(Array.from(event.dataTransfer.files));
              }}
            >
              {rows.length === 0 ? (
                <div
                  className={`flex flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed px-6 py-10 text-center transition-colors ${
                    dragging ? 'border-brand-600 bg-brand-50' : 'border-stone-300 bg-stone-50'
                  }`}
                >
                  <Upload className="size-8 text-stone-400" aria-hidden />
                  <p className="text-sm text-stone-600">Arraste os áudios gravados para cá (vários de uma vez)</p>
                  <Button variant="primary" onClick={() => input.current?.click()} icon={<Upload className="size-4" />}>
                    Escolher arquivos
                  </Button>
                  <p className="text-xs text-stone-500">{acceptedAudio.join(' ')}</p>
                </div>
              ) : (
                <>
                  <ul className={`space-y-2 rounded-xl ${dragging ? 'ring-2 ring-brand-600' : ''}`} aria-label="Áudios">
                    {rows.map((row) => (
                      <AudioRow
                        key={row.key}
                        row={row}
                        durationMs={durationOf(row.hash)}
                        playing={row.hash !== undefined && preview.playing === row.hash}
                        onPreview={() => row.hash && preview.toggle(row.hash)}
                        onName={(name) => update(row.key, { name })}
                        onRemove={() => setRows((list) => list.filter((r) => r.key !== row.key))}
                        locked={done || create.isPending}
                      />
                    ))}
                  </ul>
                  <p className="text-xs text-stone-500">
                    {ready.length} de {rows.length} {rows.length === 1 ? 'pronto' : 'prontos'}
                    {failed > 0 && ` · ${failed} com erro (ficam de fora; remova ou envie de novo)`}. Arraste mais
                    arquivos para cá para somar.
                  </p>
                </>
              )}
              <input
                ref={input}
                type="file"
                multiple
                accept={acceptedAudio.join(',')}
                className="hidden"
                data-testid="offers-batch-input"
                onChange={(event) => {
                  const files = Array.from(event.target.files ?? []);
                  event.target.value = '';
                  add(files);
                }}
              />
            </div>
          </Card>
          <RulesCard
            rules={shared.rules}
            hasAnchors={false}
            stores={chosenStores}
            onChange={(rules) => change({ rules })}
          />
        </div>
        <div className="space-y-6">
          <StoresCard storeIds={shared.storeIds} savedStoreIds={[]} onChange={(storeIds) => change({ storeIds })} />
          <Card>
            <CardHeader title="Validade" />
            <div className="space-y-4 px-5 py-4">
              <Field label="Começa">
                <Input
                  type="datetime-local"
                  value={shared.validFrom}
                  onChange={(e) => change({ validFrom: e.target.value })}
                />
              </Field>
              <Field label="Termina" hint="Sugestão: 30 dias. Depois disso as ofertas param de tocar sozinhas.">
                <Input
                  type="datetime-local"
                  value={shared.validUntil}
                  onChange={(e) => change({ validUntil: e.target.value })}
                />
              </Field>
            </div>
          </Card>
          <DuckCard value={shared.duckLevel} onChange={(duckLevel) => change({ duckLevel })} />
        </div>
      </div>
      {leaveDialog}
    </>
  );
}

function AudioRow({
  row,
  durationMs,
  playing,
  locked,
  onPreview,
  onName,
  onRemove,
}: {
  row: Row;
  durationMs: number | null;
  playing: boolean;
  locked: boolean;
  onPreview: () => void;
  onName: (name: string) => void;
  onRemove: () => void;
}) {
  const working = row.status === 'waiting' || row.status === 'sending' || row.status === 'processing';
  return (
    <li
      className={`rounded-lg px-3 py-2.5 ring-1 ${row.status === 'error' ? 'bg-red-50/60 ring-red-200' : 'bg-white ring-stone-200'}`}
    >
      <div className="flex items-center gap-2">
        {row.status === 'ready' ? (
          <IconButton label={playing ? 'Parar pré-escuta' : `Ouvir ${row.name}`} onClick={onPreview}>
            {playing ? <Pause className="size-4 text-brand-700" /> : <Play className="size-4" />}
          </IconButton>
        ) : row.status === 'error' ? (
          <CircleAlert className="mx-2 size-4 shrink-0 text-red-600" aria-hidden />
        ) : (
          <span className="mx-2 size-4 shrink-0 animate-pulse rounded-full bg-stone-200" aria-hidden />
        )}
        <label className="sr-only" htmlFor={`${row.key}-name`}>
          Nome da oferta de {row.file.name}
        </label>
        <input
          id={`${row.key}-name`}
          value={row.name}
          maxLength={80}
          disabled={locked || row.status === 'error'}
          onChange={(e) => onName(e.target.value)}
          className="min-w-0 flex-1 rounded-md border-0 bg-transparent px-2 py-1 text-sm font-medium text-stone-900 ring-1 ring-transparent hover:ring-stone-200 focus:ring-2 focus:ring-brand-600 focus:outline-none disabled:text-stone-500"
        />
        <span className="shrink-0 text-xs text-stone-500 tabular-nums">
          {row.status === 'waiting'
            ? 'Na espera'
            : row.status === 'sending'
              ? `Enviando ${Math.round(row.progress * 100)}%`
              : row.status === 'processing'
                ? row.job?.status === 'processing'
                  ? 'Processando…'
                  : 'Na fila do Hub…'
                : row.status === 'ready'
                  ? formatDuration(durationMs)
                  : 'Erro'}
        </span>
        <IconButton
          label={`Tirar ${row.name} da lista`}
          onClick={onRemove}
          disabled={locked || row.status === 'sending'}
        >
          <X className="size-4" />
        </IconButton>
      </div>
      {working && (
        <div className="mt-2 pl-10">
          {row.status === 'sending' ? (
            <ProgressBar value={row.progress * 100} label={`Enviando ${row.file.name}`} />
          ) : row.status === 'processing' ? (
            <ProgressBar label={`Processando ${row.file.name}`} />
          ) : (
            <ProgressBar value={0} label={`${row.file.name} na espera`} />
          )}
        </div>
      )}
      {row.status === 'error' && (
        <div className="mt-1.5 pl-10">
          <ErrorNote>
            {row.file.name}: {row.error}
          </ErrorNote>
        </div>
      )}
    </li>
  );
}
