import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Bluetooth,
  Link2,
  ListMusic,
  Megaphone,
  Pause,
  Play,
  PlayCircle,
  Search,
  SkipForward,
  Volume2,
} from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useStoreTracks } from '../../api/queries';
import type { StoreDetail, StoreSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { Button, Card, CardHeader, EmptyState, ErrorNote, Input, SoonTag, Spinner } from '../../components/ui';
import { useCommand } from '../../hooks/useCommand';
import { useNow } from '../../hooks/useNow';
import { formatAgo, formatDuration, formatMegabytes, matchesSearch } from '../../lib/format';
import { bluetoothStatus, syncStatus } from '../../lib/status';
import { NowPlaying } from '../dashboard/NowPlaying';
import { StoreAlerts } from '../dashboard/StoreAlerts';
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
            actions={<SoonTag stage={5} />}
          />
          <p className="px-5 py-4 text-sm text-stone-500">
            Próxima oferta prevista, última oferta tocada e "tocar oferta agora" chegam com a tela de Ofertas.
          </p>
        </Card>
      </div>
    </div>
  );
}

function Controls({ store }: { store: StoreSummary }) {
  const { run, busy } = useCommand(store.id);
  const [choosing, setChoosing] = useState(false);
  const offline = !store.online;
  const playing = store.player === 'playing';

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
          disabled={offline}
          busy={busy === 'reconnectBluetooth'}
          onClick={() => void run({ command: 'reconnectBluetooth' })}
          icon={<Bluetooth className="size-5" />}
        >
          Reconectar caixa
        </Button>
        <Button size="lg" disabled title="Chega na Etapa 5, com as ofertas" icon={<Megaphone className="size-5" />}>
          Tocar oferta agora <SoonTag stage={5} />
        </Button>
      </div>
      <VolumeSlider
        store={store}
        disabled={offline}
        onChange={(volume) => run({ command: 'setVolume', volume }, true)}
      />
      {offline && <p className="text-sm text-stone-500">Os controles voltam quando a loja se conectar ao Hub.</p>}
      {choosing && <PlayNowDialog store={store} onClose={() => setChoosing(false)} />}
    </div>
  );
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
                <span className="block truncate text-xs text-stone-500">{track.artist ?? 'Artista desconhecido'}</span>
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
          {store.open ? 'A loja ainda não informou a fila.' : 'Fora do horário da agenda a loja fica em silêncio.'}
        </EmptyState>
      ) : (
        <ol className="divide-y divide-stone-100">
          {detail.queue.map((track, index) => (
            <li key={`${index}-${track.hash}`} className="flex items-center gap-3 px-5 py-2 text-sm">
              <span className="w-5 text-right text-xs text-stone-400 tabular-nums">{index + 1}</span>
              <span className="min-w-0 flex-1">
                <span className="block truncate font-medium text-stone-900">{track.title}</span>
                <span className="block truncate text-xs text-stone-500">{track.artist ?? 'Artista desconhecido'}</span>
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
        <Row label="Configuração">{sync.label}</Row>
        <Row label="Horário">{store.open ? 'Dentro do horário da agenda' : 'Fora do horário da agenda'}</Row>
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
