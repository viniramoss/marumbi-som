import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  ArrowLeft,
  Headphones,
  Pause,
  Play,
  RotateCcw,
  Scissors,
  SquareArrowLeft,
  SquareArrowRight,
  Trash2,
  ZoomIn,
  ZoomOut,
} from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { errorMessage } from '../../api/client';
import { keys, useEditTrack, useTracks, useUploads } from '../../api/queries';
import { waitForIngestion } from '../../api/upload';
import type { IngestionJob, Track } from '../../api/types';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import {
  Button,
  Card,
  CardHeader,
  EmptyState,
  ErrorNote,
  IconButton,
  Input,
  PageHeader,
  ProgressBar,
  Spinner,
} from '../../components/ui';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { formatDuration } from '../../lib/format';
import {
  clamp,
  formatPrecise,
  MinDurationMs,
  normalizeCuts,
  parsePrecise,
  removedMs,
  skipTarget,
  type Cut,
} from './editor/cuts';
import { useWaveform, type Waveform } from './editor/useWaveform';
import { WaveformView } from './editor/WaveformView';

const Zooms = [1, 2, 4, 8, 16] as const;
/** Quanto se ouve antes e depois da emenda em "Ouvir como fica". */
const SeamPreviewMs = 3_000;

/** Editor de música (D34): marcar trechos na onda e salvar a música sem eles. */
export function TrackEditorPage() {
  const { hash = '' } = useParams();
  const tracks = useTracks();
  const uploads = useUploads();
  const navigate = useNavigate();
  const track = tracks.data?.find((t) => t.hash === hash);
  // Editada (aqui ou em outro aparelho): a música virou outro arquivo, então a tela segue para ele.
  const replacedBy = track
    ? undefined
    : uploads.data?.find((j) => j.editOf === hash && j.status === 'done' && j.hash && j.hash !== hash)?.hash;

  useEffect(() => {
    if (replacedBy) void navigate(`/biblioteca/${replacedBy}`, { replace: true });
  }, [replacedBy, navigate]);

  if (tracks.isPending || (!track && (tracks.isFetching || uploads.isPending || replacedBy))) return <Spinner />;
  if (tracks.isError) return <ErrorNote>{errorMessage(tracks.error)}</ErrorNote>;
  if (!track) {
    return (
      <EmptyState icon={<Scissors className="size-10" />} title="Música não encontrada">
        Ela pode ter sido removida ou editada de novo. <Link to="/biblioteca">Voltar para a Biblioteca</Link>
      </EmptyState>
    );
  }
  return <TrackEditor key={track.hash} track={track} />;
}

function TrackEditor({ track }: { track: Track }) {
  const waveform = useWaveform(track.hash);
  const navigate = useNavigate();
  return (
    <>
      <PageHeader
        title={`Editar ${track.title}`}
        description={`${track.originalName} · ${formatDuration(track.durationMs)}`}
        actions={
          <Button variant="ghost" icon={<ArrowLeft className="size-4" />} onClick={() => void navigate('/biblioteca')}>
            Biblioteca
          </Button>
        }
      />
      {waveform.status === 'ready' ? (
        <TrackEditorView track={track} waveform={waveform.waveform} />
      ) : (
        <Card>
          <div className="space-y-2 p-5 text-sm text-stone-600">
            {waveform.status === 'error' ? (
              <ErrorNote>{waveform.message}</ErrorNote>
            ) : waveform.status === 'loading' ? (
              <>
                <p>
                  Baixando a música do Hub
                  {waveform.progress !== null && ` · ${Math.round(waveform.progress * 100)}%`}
                </p>
                <ProgressBar
                  value={waveform.progress === null ? undefined : waveform.progress * 100}
                  label="Baixando a música"
                />
              </>
            ) : (
              <>
                <p>Desenhando a onda…</p>
                <ProgressBar label="Desenhando a onda" />
              </>
            )}
          </div>
        </Card>
      )}
    </>
  );
}

/** A tela do editor com a onda já pronta (separada para os testes, que não decodificam áudio). */
export function TrackEditorView({ track, waveform }: { track: Track; waveform: Waveform }) {
  const duration = waveform.durationMs;
  const audio = useRef<HTMLAudioElement | null>(null);
  const stopAt = useRef<number | null>(null);
  const [cuts, setCuts] = useState<Cut[]>([]);
  const [selected, setSelected] = useState<number | null>(null);
  const [pending, setPending] = useState<number | null>(null);
  const [position, setPosition] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [skip, setSkip] = useState(true);
  const [zoom, setZoom] = useState<(typeof Zooms)[number]>(1);
  const [job, setJob] = useState<IngestionJob | null>(null);
  const [restoring, setRestoring] = useState(false);
  const [confirmRestore, setConfirmRestore] = useState(false);
  const edit = useEditTrack();
  const working = job?.status === 'queued' || job?.status === 'processing';
  const navigate = useNavigate();
  const client = useQueryClient();
  const toast = useToast();
  const cutsRef = useRef(cuts);
  const skipRef = useRef(skip);
  const alive = useRef(true);
  useEffect(() => {
    cutsRef.current = cuts;
    skipRef.current = skip;
  }, [cuts, skip]);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);

  const remaining = duration - removedMs(cuts);
  const tooShort = cuts.length > 0 && remaining < MinDurationMs;
  const failed = job && (job.status === 'rejected' || job.status === 'failed') ? job : null;
  // Depois de salvar, a tela troca para a música editada sem perguntar (o pedido já está no Hub).
  const unsaved = useUnsavedChanges(
    cuts.length > 0 && (job === null || failed !== null),
    'Os trechos marcados ainda não foram salvos.',
  );

  useEffect(() => {
    const element = new Audio(`/api/tracks/${track.hash}/audio`);
    element.preload = 'auto';
    element.onended = () => setPlaying(false);
    element.onpause = () => setPlaying(false);
    element.onplay = () => setPlaying(true);
    audio.current = element;
    return () => {
      element.pause();
      element.src = '';
    };
  }, [track.hash]);

  // Agulha acompanhando a música; com "pular os trechos", salta cada trecho marcado como no arquivo editado.
  useEffect(() => {
    if (!playing) return;
    let frame = 0;
    const tick = () => {
      const element = audio.current;
      if (!element) return;
      let ms = element.currentTime * 1000;
      const target = skipRef.current ? skipTarget(ms, cutsRef.current) : null;
      if (target !== null) {
        if (target >= duration - 50) {
          element.pause();
        } else {
          element.currentTime = target / 1000;
          ms = target;
        }
      }
      if (stopAt.current !== null && ms >= stopAt.current) {
        element.pause();
        stopAt.current = null;
      }
      setPosition(ms);
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, [playing, duration]);

  const seek = useCallback(
    (ms: number) => {
      const value = clamp(ms, 0, duration);
      if (audio.current) audio.current.currentTime = value / 1000;
      setPosition(value);
    },
    [duration],
  );

  const play = useCallback((from?: number, until?: number) => {
    const element = audio.current;
    if (!element) return;
    if (from !== undefined) {
      element.currentTime = from / 1000;
      setPosition(from);
    }
    stopAt.current = until ?? null;
    void Promise.resolve(element.play()).catch(() => setPlaying(false));
  }, []);

  const toggle = useCallback(() => {
    if (playing) audio.current?.pause();
    else play();
  }, [playing, play]);

  const change = (next: Cut[]) => {
    setCuts(normalizeCuts(next, duration));
    setSelected(null);
  };

  const markStart = useCallback(() => setPending(position), [position]);
  const markEnd = useCallback(() => {
    if (pending === null) return;
    setCuts((list) => normalizeCuts([...list, { startMs: pending, endMs: position }], duration));
    setPending(null);
  }, [pending, position, duration]);

  // Atalhos: espaço toca/pausa, I marca o início e O o fim do trecho (fora dos campos de texto).
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (target?.closest('input, textarea, select, button, [role="dialog"]')) return;
      if (event.key === ' ') {
        event.preventDefault();
        toggle();
      } else if (event.key === 'i' || event.key === 'I') markStart();
      else if (event.key === 'o' || event.key === 'O') markEnd();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [toggle, markStart, markEnd]);

  // O Hub refaz a música na fila da ingestão; ao terminar, abre a editada (outro arquivo, outro endereço).
  const save = async (original: boolean) => {
    audio.current?.pause();
    try {
      const created = await edit.mutateAsync({ hash: track.hash, cuts: original ? [] : cuts, original });
      setRestoring(original);
      setConfirmRestore(false);
      setJob(created);
      const done = await waitForIngestion(created.id, (current) => {
        if (alive.current) setJob(current);
      });
      if (done.status !== 'done' || !done.hash) return;
      toast.success(
        original
          ? `${track.title} voltou a ser a música enviada.`
          : `${track.title} editada: as lojas recebem na próxima sincronização.`,
      );
      // Se a lista de músicas já chegou sem a anterior, a página seguiu para a editada sozinha.
      if (!alive.current) return;
      setCuts([]);
      await client.invalidateQueries({ queryKey: keys.tracks });
      if (done.hash !== track.hash) void navigate(`/biblioteca/${done.hash}`, { replace: true });
      else setJob(null);
    } catch (error) {
      if (alive.current) {
        setJob(null);
        toast.error(errorMessage(error));
      }
    }
  };

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="Trechos para tirar"
          subtitle="Arraste na onda para marcar um trecho, ou use Marcar início e Marcar fim enquanto ouve. A borda de um trecho ajusta arrastando."
          actions={
            <>
              <IconButton
                label="Menos zoom"
                disabled={zoom === Zooms[0]}
                onClick={() => setZoom(Zooms[Math.max(0, Zooms.indexOf(zoom) - 1)] ?? 1)}
              >
                <ZoomOut className="size-4" />
              </IconButton>
              <span className="w-8 text-center text-sm text-stone-600 tabular-nums">{zoom}×</span>
              <IconButton
                label="Mais zoom"
                disabled={zoom === Zooms.at(-1)}
                onClick={() => setZoom(Zooms[Math.min(Zooms.length - 1, Zooms.indexOf(zoom) + 1)] ?? 16)}
              >
                <ZoomIn className="size-4" />
              </IconButton>
            </>
          }
        />
        <div className="space-y-4 p-5">
          <WaveformView
            waveform={waveform}
            cuts={cuts}
            pending={pending}
            positionMs={position}
            zoom={zoom}
            selected={selected}
            onSeek={seek}
            onChange={change}
            onSelect={setSelected}
          />
          <div className="flex flex-wrap items-center gap-2">
            <Button
              variant="primary"
              onClick={toggle}
              icon={playing ? <Pause className="size-4" /> : <Play className="size-4" />}
            >
              {playing ? 'Pausar' : 'Ouvir'}
            </Button>
            <span className="min-w-28 text-sm text-stone-700 tabular-nums">
              {formatPrecise(position)} / {formatPrecise(duration)}
            </span>
            <label className="flex items-center gap-2 text-sm text-stone-700">
              <input
                type="checkbox"
                className="size-4 accent-brand-700"
                checked={skip}
                onChange={(e) => setSkip(e.target.checked)}
              />
              Pular os trechos marcados ao ouvir
            </label>
            <span className="flex-1" />
            <Button onClick={markStart} icon={<SquareArrowRight className="size-4" />}>
              Marcar início
            </Button>
            <Button onClick={markEnd} disabled={pending === null} icon={<SquareArrowLeft className="size-4" />}>
              Marcar fim
            </Button>
          </div>
          <p className="text-xs text-stone-500">
            Atalhos: espaço toca ou pausa, <kbd>I</kbd> marca o início e <kbd>O</kbd> o fim do trecho.
            {pending !== null &&
              ` Início marcado em ${formatPrecise(pending)}: toque até o fim do trecho e marque o fim.`}
          </p>

          {cuts.length > 0 && (
            <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200">
              {cuts.map((cut, index) => (
                <CutRow
                  key={`${cut.startMs}-${cut.endMs}`}
                  index={index}
                  cut={cut}
                  selected={selected === index}
                  onChange={(next) => change(cuts.map((c, i) => (i === index ? next : c)))}
                  onRemove={() => change(cuts.filter((_, i) => i !== index))}
                  onListen={() => play(cut.startMs, cut.endMs)}
                  onListenSeam={() => {
                    setSkip(true);
                    play(Math.max(0, cut.startMs - SeamPreviewMs), Math.min(duration, cut.endMs + SeamPreviewMs));
                  }}
                />
              ))}
            </ul>
          )}
        </div>

        <div className="space-y-3 border-t border-stone-100 px-5 py-4">
          {working ? (
            <div className="space-y-1.5">
              <p className="text-sm text-stone-700">
                {restoring ? 'Voltando para a música enviada' : 'Refazendo a música sem os trechos'}
                {job?.status === 'processing' ? ` · ${Math.round(job.progress * 100)}%` : ' · na fila'}
              </p>
              <ProgressBar
                value={job?.status === 'processing' ? job.progress * 100 : undefined}
                label={restoring ? 'Voltando para a original' : 'Refazendo a música'}
              />
            </div>
          ) : (
            <div className="flex flex-wrap items-center justify-between gap-3">
              <p className={`text-sm ${tooShort ? 'text-red-700' : 'text-stone-700'}`}>
                {cuts.length === 0
                  ? 'Nenhum trecho marcado.'
                  : tooShort
                    ? `Sem os trechos ficaria com ${formatDuration(remaining)}; o mínimo é 0:30.`
                    : `A música fica com ${formatDuration(remaining)} (tirando ${formatPrecise(removedMs(cuts))}).`}
              </p>
              <div className="flex flex-wrap gap-2">
                {cuts.length > 0 && <Button onClick={() => change([])}>Desmarcar tudo</Button>}
                <Button
                  variant="primary"
                  icon={<Scissors className="size-4" />}
                  disabled={cuts.length === 0 || tooShort}
                  busy={edit.isPending}
                  onClick={() => void save(false)}
                >
                  Salvar sem os trechos
                </Button>
              </div>
            </div>
          )}
          {failed && <ErrorNote>{failed.error ?? 'Não foi possível editar a música.'}</ErrorNote>}
          <p className="text-xs text-stone-500">
            O Hub refaz a música a partir do arquivo enviado, sem perder qualidade. Ela continua com o mesmo número e
            nas mesmas playlists, e as ofertas presas nela acompanham o corte.
          </p>
        </div>
      </Card>

      {track.edited && (
        <Card>
          <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
            <p className="text-sm text-stone-700">
              Esta música já foi editada: {formatPrecise(track.removedMs)} a menos que o arquivo enviado. Os trechos
              novos se somam aos de antes.
            </p>
            <Button icon={<RotateCcw className="size-4" />} disabled={working} onClick={() => setConfirmRestore(true)}>
              Voltar para a original
            </Button>
          </div>
        </Card>
      )}

      {confirmRestore && (
        <ConfirmDialog
          title="Voltar para a original?"
          confirmLabel="Voltar para a original"
          danger={false}
          busy={edit.isPending}
          onCancel={() => setConfirmRestore(false)}
          onConfirm={() => void save(true)}
        >
          <p>
            <strong>{track.title}</strong> volta a ser exatamente como foi enviada, com os{' '}
            {formatPrecise(track.removedMs)} tirados de volta. As playlists e as ofertas presas nela acompanham.
          </p>
        </ConfirmDialog>
      )}
      {unsaved}
    </div>
  );
}

function CutRow({
  index,
  cut,
  selected,
  onChange,
  onRemove,
  onListen,
  onListenSeam,
}: {
  index: number;
  cut: Cut;
  selected: boolean;
  onChange: (cut: Cut) => void;
  onRemove: () => void;
  onListen: () => void;
  onListenSeam: () => void;
}) {
  return (
    <li className={`flex flex-wrap items-center gap-3 px-3 py-2 text-sm ${selected ? 'bg-red-50' : ''}`}>
      <span className="w-20 font-medium text-stone-800">Trecho {index + 1}</span>
      <TimeInput
        key={`s${cut.startMs}`}
        label={`Início do trecho ${index + 1}`}
        value={cut.startMs}
        onChange={(startMs) => onChange({ ...cut, startMs })}
      />
      <span className="text-stone-500">até</span>
      <TimeInput
        key={`e${cut.endMs}`}
        label={`Fim do trecho ${index + 1}`}
        value={cut.endMs}
        onChange={(endMs) => onChange({ ...cut, endMs })}
      />
      <span className="text-stone-500 tabular-nums">({formatPrecise(cut.endMs - cut.startMs)})</span>
      <span className="flex-1" />
      <Button size="sm" variant="ghost" icon={<Play className="size-4" />} onClick={onListen}>
        Ouvir o trecho
      </Button>
      <Button size="sm" variant="ghost" icon={<Headphones className="size-4" />} onClick={onListenSeam}>
        Ouvir como fica
      </Button>
      <IconButton label={`Desmarcar o trecho ${index + 1}`} onClick={onRemove}>
        <Trash2 className="size-4" />
      </IconButton>
    </li>
  );
}

/** Tempo editável como 1:05,2; vale ao sair do campo ou com Enter. Quem usa remonta (key) quando o valor muda. */
function TimeInput({ label, value, onChange }: { label: string; value: number; onChange: (ms: number) => void }) {
  const [text, setText] = useState(formatPrecise(value));
  const commit = () => {
    const parsed = parsePrecise(text);
    if (parsed === null || parsed === value) setText(formatPrecise(value));
    else onChange(parsed);
  };
  return (
    <Input
      aria-label={label}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') commit();
      }}
      className="!w-24 text-center tabular-nums"
    />
  );
}
