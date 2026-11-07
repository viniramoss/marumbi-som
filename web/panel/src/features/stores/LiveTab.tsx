import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Bluetooth,
  Link2,
  ListMusic,
  Megaphone,
  Pause,
  Play,
  PlayCircle,
  RefreshCw,
  Search,
  SkipForward,
  Volume2,
} from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useStoreOffers, useStoreTracks } from '../../api/queries';
import type { StoreDetail, StoreSummary } from '../../api/types';
import { ConfirmDialog, Modal } from '../../components/Modal';
import { SyncProgress } from '../../components/SyncProgress';
import { useToast } from '../../components/Toasts';
import { Button, Card, CardHeader, EmptyState, ErrorNote, Input, ProgressBar, Spinner } from '../../components/ui';
import { useCommand } from '../../hooks/useCommand';
import { useNow } from '../../hooks/useNow';
import { formatAgo, formatDateTime, formatDuration, formatMegabytes, matchesSearch } from '../../lib/format';
import { bluetoothStatus, syncStatus } from '../../lib/status';
import { NowPlaying } from '../dashboard/NowPlaying';
import { OfferInfo } from '../dashboard/OfferInfo';
import { NoScheduleNotice, StoreAlerts } from '../dashboard/StoreAlerts';
import { EnrollCard } from './EnrollCode';

/** Aba "Ao vivo": controle da loja agora, fila e situação do PC. */
export function LiveTab({ detail }: { detail: StoreDetail }) {
  const store = detail.status;
  if (!store.enrolled) return <EnrollCard store={store} />;

  return (
    <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_360px]">
      <div className="space-y-6">
        <Card>
          <CardHeader title="Tocando agora" />
          <div className="space-y-5 px-5 py-4">
            <StoreAlerts alerts={store.alerts} />
            <NoScheduleNotice store={store} />
            <NowPlaying store={store} large />
            <Controls store={store} />
          </div>
        </Card>
        <QueueCard detail={detail} />
      </div>
      <div className="space-y-6">
        <SituationCard store={store} />
        <Card>
          <CardHeader
            title={
              <span className="inline-flex items-center gap-2">
                <Megaphone className="size-4 text-stone-400" /> Ofertas
              </span>
            }
          />
          <div className="space-y-2 px-5 py-4">
            <OfferInfo store={store} showEmpty />
            <p className="text-xs text-stone-500">
              A previsão vem da loja e muda se alguém pular, pausar ou se a agenda trocar de playlist.
            </p>
          </div>
        </Card>
      </div>
    </div>
  );
}

function Controls({ store }: { store: StoreSummary }) {
  const { run, busy } = useCommand(store.id);
  const [choosing, setChoosing] = useState(false);
  const [offering, setOffering] = useState(false);
  const [confirmAudio, setConfirmAudio] = useState(false);
  const offline = !store.online;
  const playing = store.player === 'playing';
  const restarting = store.bluetooth === 'restartingAudio';
  useAudioRestartOutcome(store);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="primary"
          size="lg"
          disabled={offline}
          busy={busy === 'play' || busy === 'pause'}
          onClick={() => void run({ command: playing ? 'pause' : 'play' })}
          icon={playing ? <Pause className="size-5" /> : <Play className="size-5" />}
        >
          {playing ? 'Pausar' : 'Tocar'}
        </Button>
        <Button
          size="lg"
          disabled={offline}
          busy={busy === 'next'}
          onClick={() => void run({ command: 'next' })}
          icon={<SkipForward className="size-5" />}
        >
          Pular
        </Button>
        <Button size="lg" disabled={offline} onClick={() => setChoosing(true)} icon={<PlayCircle className="size-5" />}>
          Tocar agora…
        </Button>
        <Button
          size="lg"
          disabled={offline || restarting}
          busy={busy === 'reconnectBluetooth'}
          onClick={() => void run({ command: 'reconnectBluetooth' })}
          icon={<Bluetooth className="size-5" />}
        >
          Reconectar caixa
        </Button>
        <Button
          size="lg"
          disabled={offline || restarting}
          busy={busy === 'restartAudio' || restarting}
          title={
            offline
              ? undefined
              : 'Para quando a caixa aparece conectada e a loja está sem som: reinicia o áudio e o Bluetooth do Windows sem reiniciar o PC.'
          }
          onClick={() => setConfirmAudio(true)}
          icon={<RefreshCw className="size-5" />}
        >
          Reiniciar áudio
        </Button>
        <Button
          size="lg"
          disabled={offline || !playing}
          title={!offline && !playing ? 'A oferta toca por cima da música: dê play antes.' : undefined}
          onClick={() => setOffering(true)}
          icon={<Megaphone className="size-5" />}
        >
          Tocar oferta agora…
        </Button>
      </div>
      {restarting && <AudioRestartProgress />}
      <VolumeSlider
        store={store}
        disabled={offline}
        onChange={(volume) => run({ command: 'setVolume', volume }, true)}
      />
      {offline && <p className="text-sm text-stone-500">Os controles voltam quando a loja se conectar ao Hub.</p>}
      {choosing && <PlayNowDialog store={store} onClose={() => setChoosing(false)} />}
      {offering && <PlayOfferDialog store={store} onClose={() => setOffering(false)} />}
      {confirmAudio && (
        <ConfirmDialog
          title="Reiniciar o áudio da loja?"
          confirmLabel="Reiniciar áudio"
          danger={false}
          busy={busy === 'restartAudio'}
          onCancel={() => setConfirmAudio(false)}
          onConfirm={() => void run({ command: 'restartAudio' }).then(() => setConfirmAudio(false))}
        >
          <p>
            O PC da {store.name} reinicia o áudio e o Bluetooth do Windows e reconecta a caixa, sem reiniciar o PC. A
            música para por cerca de 30 segundos e depois volta sozinha do mesmo ponto.
          </p>
          <p className="text-stone-500">
            Use quando a caixa aparece conectada e o painel diz que está tocando, mas a loja está sem som. Se não
            resolver, reinicie o PC da loja.
          </p>
        </ConfirmDialog>
      )}
    </div>
  );
}

/** Andamento do "reiniciar áudio" (D37): barra em movimento até a caixa voltar. */
function AudioRestartProgress() {
  return (
    <div className="space-y-2 rounded-lg bg-sky-50 px-4 py-3 ring-1 ring-sky-200" role="status">
      <p className="text-sm text-sky-900">
        <span className="font-medium">Reiniciando o áudio e o Bluetooth do Windows…</span> A música volta sozinha do
        mesmo ponto quando a caixa reconectar.
      </p>
      <ProgressBar label="Reiniciando o áudio da loja" />
    </div>
  );
}

/** Avisa como terminou o "reiniciar áudio" quando o status sai de restartingAudio com a tela aberta. */
function useAudioRestartOutcome(store: StoreSummary) {
  const toast = useToast();
  const previous = useRef(store.bluetooth);
  useEffect(() => {
    const before = previous.current;
    previous.current = store.bluetooth;
    if (before !== 'restartingAudio' || store.bluetooth === 'restartingAudio') return;
    if (store.bluetooth === 'connected') {
      toast.success('Áudio reiniciado e caixa conectada.');
    } else if (store.online) {
      toast.error(
        'A caixa não voltou depois do reinício do áudio. O agente segue tentando; se não voltar, reinicie o PC da loja.',
      );
    }
  }, [store.bluetooth, store.online, toast]);
}

/** Volume ao vivo (0 a 100). Manda o valor 350 ms depois de parar de arrastar e mostra o novo até a loja confirmar. */
function VolumeSlider({
  store,
  disabled,
  onChange,
}: {
  store: StoreSummary;
  disabled: boolean;
  onChange: (volume: number) => Promise<boolean>;
}) {
  const [pending, setPending] = useState<number | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const value = pending ?? store.volume ?? 0;

  // A loja confirmou o volume novo: volta a mostrar o que ela informa.
  if (pending !== null && store.volume === pending) setPending(null);
  useEffect(() => () => clearTimeout(timer.current), []);

  const change = (volume: number) => {
    setPending(volume);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      void onChange(volume).then((ok) => {
        // Sem confirmação em 4 s (ou com erro), volta a mostrar o volume que a loja informou.
        timer.current = setTimeout(() => setPending(null), ok ? 4000 : 0);
      });
    }, 350);
  };

  return (
    <div className="flex items-center gap-3">
      <Volume2 className="size-5 shrink-0 text-stone-500" aria-hidden />
      <input
        type="range"
        min={0}
        max={100}
        step={1}
        value={value}
        disabled={disabled}
        aria-label="Volume da loja"
        onChange={(e) => change(Number(e.target.value))}
        className="h-2 w-full max-w-md cursor-pointer accent-brand-700 disabled:cursor-not-allowed"
      />
      <span className="w-10 text-right text-sm font-semibold tabular-nums">{store.volume === null ? '–' : value}</span>
    </div>
  );
}

function PlayNowDialog({ store, onClose }: { store: StoreSummary; onClose: () => void }) {
  const tracks = useStoreTracks(store.id, true);
  const [query, setQuery] = useState('');
  const { run, busy } = useCommand(store.id);
  const [chosen, setChosen] = useState<string | null>(null);
  const list = useMemo(
    () => (tracks.data ?? []).filter((t) => matchesSearch(query, t.title, t.artist)),
    [tracks.data, query],
  );

  const play = async (hash: string) => {
    setChosen(hash);
    if (await run({ command: 'playNow', hash })) onClose();
  };

  return (
    <Modal title="Tocar agora" wide onClose={onClose}>
      <p className="mb-3 text-sm text-stone-500">
        A música escolhida entra em 1 s e depois a fila segue normal. Aparecem as músicas que já estão no PC da loja.
      </p>
      <div className="relative mb-3">
        <Search
          className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-stone-400"
          aria-hidden
        />
        <Input
          type="search"
          placeholder="Buscar por título ou artista"
          aria-label="Buscar música"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="pl-9"
        />
      </div>
      {tracks.isPending ? (
        <Spinner />
      ) : tracks.isError ? (
        <ErrorNote>{errorMessage(tracks.error)}</ErrorNote>
      ) : list.length === 0 ? (
        <EmptyState title={query ? 'Nenhuma música encontrada' : 'Nenhuma música na configuração desta loja'} />
      ) : (
        <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200">
          {list.map((track) => (
            <li key={track.hash} className="flex items-center gap-3 px-3 py-2 text-sm">
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-stone-900">{track.title}</span>
                {track.artist && <span className="block truncate text-xs text-stone-500">{track.artist}</span>}
              </span>
              <span className="w-12 text-right text-stone-600 tabular-nums">{formatDuration(track.durationMs)}</span>
              <Button
                size="sm"
                variant={store.nowPlaying?.hash === track.hash ? 'ghost' : 'secondary'}
                busy={busy === 'playNow' && chosen === track.hash}
                disabled={busy !== null}
                onClick={() => void play(track.hash)}
                icon={<Play className="size-4" />}
              >
                Tocar
              </Button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  );
}

/** "Tocar oferta agora" (seção 7.2): ofertas vigentes da configuração aplicada da loja (D26 g). */
function PlayOfferDialog({ store, onClose }: { store: StoreSummary; onClose: () => void }) {
  const offers = useStoreOffers(store.id, true);
  const { run, busy } = useCommand(store.id);
  const [chosen, setChosen] = useState<string | null>(null);

  const play = async (key: string) => {
    setChosen(key);
    if (await run({ command: 'playOffer', offerKey: key })) onClose();
  };

  return (
    <Modal title="Tocar oferta agora" onClose={onClose}>
      <p className="mb-3 text-sm text-stone-500">
        A música abaixa e a oferta entra em instantes, sem esperar o intervalo mínimo entre ofertas.
      </p>
      {offers.isPending ? (
        <Spinner />
      ) : offers.isError ? (
        <ErrorNote>{errorMessage(offers.error)}</ErrorNote>
      ) : offers.data.length === 0 ? (
        <EmptyState icon={<Megaphone className="size-8" />} title="Nenhuma oferta valendo nesta loja agora">
          A oferta precisa estar ativa, dentro da validade e com esta loja escolhida, e a loja precisa ter recebido a
          configuração.
        </EmptyState>
      ) : (
        <ul className="divide-y divide-stone-100 rounded-lg ring-1 ring-stone-200">
          {offers.data.map((offer) => (
            <li key={offer.key} className="flex items-center gap-3 px-3 py-2 text-sm">
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-stone-900">{offer.name}</span>
                <span className="block truncate text-xs text-stone-500">
                  {formatDuration(offer.durationMs)} · até {formatDateTime(offer.validUntil)}
                </span>
              </span>
              <Button
                size="sm"
                busy={busy === 'playOffer' && chosen === offer.key}
                disabled={busy !== null}
                onClick={() => void play(offer.key)}
                icon={<Play className="size-4" />}
              >
                Tocar
              </Button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  );
}

function QueueCard({ detail }: { detail: StoreDetail }) {
  const store = detail.status;
  return (
    <Card>
      <CardHeader
        title="Fila"
        subtitle={store.playlistName ? `Próximas da playlist ${store.playlistName}` : 'Próximas músicas'}
      />
      {detail.queue.length === 0 ? (
        <EmptyState icon={<ListMusic className="size-8" />} title="Fila vazia">
          {!store.hasSchedule
            ? 'Sem faixas na agenda a loja não toca nada.'
            : store.open
              ? 'A loja ainda não informou a fila.'
              : 'Fora do horário da agenda a loja fica em silêncio.'}
        </EmptyState>
      ) : (
        <ol className="divide-y divide-stone-100">
          {detail.queue.map((track, index) => (
            <li key={`${index}-${track.hash}`} className="flex items-center gap-3 px-5 py-2 text-sm">
              <span className="w-5 text-right text-xs text-stone-400 tabular-nums">{index + 1}</span>
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-stone-900">{track.title}</span>
                {track.artist && <span className="block truncate text-xs text-stone-500">{track.artist}</span>}
              </span>
              <span className="text-stone-500 tabular-nums">{formatDuration(track.durationMs)}</span>
            </li>
          ))}
        </ol>
      )}
    </Card>
  );
}

function SituationCard({ store }: { store: StoreSummary }) {
  const now = useNow(5000);
  const bluetooth = bluetoothStatus(store);
  const sync = syncStatus(store);
  return (
    <Card>
      <CardHeader title="Situação" />
      <dl className="divide-y divide-stone-100 text-sm">
        <Row label="Conexão">{store.online ? 'Conectada' : `Último sinal ${formatAgo(store.lastSeenAt, now)}`}</Row>
        <Row label="Caixa de som">{bluetooth.label}</Row>
        <Row label="Configuração">
          {sync.label}
          <SyncProgress store={store} className="mt-1 ml-auto w-32" />
        </Row>
        <Row label="Horário">
          {!store.hasSchedule ? 'Sem agenda' : store.open ? 'Dentro do horário da agenda' : 'Fora do horário da agenda'}
        </Row>
        <Row label="Playlist da agenda">{store.playlistName ?? '–'}</Row>
        <Row label="Disco livre">{formatMegabytes(store.diskFreeMb)}</Row>
        <Row label="Versão do agente">{store.agentVersion ?? '–'}</Row>
        <Row label="Vínculo">
          <span className="inline-flex items-center gap-1.5">
            <Link2 className="size-3.5 text-stone-400" aria-hidden /> Vinculada
          </span>
        </Row>
      </dl>
    </Card>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 px-5 py-2.5">
      <dt className="text-stone-500">{label}</dt>
      <dd className="text-right font-medium text-stone-800">{children}</dd>
    </div>
  );
}
