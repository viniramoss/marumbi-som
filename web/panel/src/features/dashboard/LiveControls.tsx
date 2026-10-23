import { Pause, Play, SkipForward, Volume1, Volume2 } from 'lucide-react';
import type { StoreSummary } from '../../api/types';
import { Button } from '../../components/ui';
import { useCommand } from '../../hooks/useCommand';

export const VolumeStep = 5;

/** Botões rápidos do cartão (seção 7.2): tocar/pausar, pular e volume −/+. */
export function LiveControls({ store }: { store: StoreSummary }) {
  const { run, busy } = useCommand(store.id);
  const offline = !store.online;
  const volume = store.volume ?? 0;
  const playing = store.player === 'playing';

  return (
    <div className="flex flex-wrap items-center gap-2">
      <Button
        variant="primary"
        size="sm"
        disabled={offline}
        busy={busy === 'play' || busy === 'pause'}
        onClick={() => void run({ command: playing ? 'pause' : 'play' }, true)}
        icon={playing ? <Pause className="size-4" /> : <Play className="size-4" />}
      >
        {playing ? 'Pausar' : 'Tocar'}
      </Button>
      <Button
        size="sm"
        disabled={offline}
        busy={busy === 'next'}
        onClick={() => void run({ command: 'next' })}
        icon={<SkipForward className="size-4" />}
      >
        Pular
      </Button>
      <div className="ml-auto flex items-center gap-1 rounded-lg bg-stone-100 p-0.5">
        <Button
          variant="ghost"
          size="sm"
          aria-label="Diminuir volume"
          title="Diminuir volume"
          disabled={offline || volume <= 0}
          busy={busy === 'setVolume'}
          onClick={() => void run({ command: 'setVolume', volume: Math.max(0, volume - VolumeStep) }, true)}
          icon={<Volume1 className="size-4" />}
        />
        <span className="w-9 text-center text-sm font-semibold tabular-nums" aria-label="Volume">
          {store.volume ?? '–'}
        </span>
        <Button
          variant="ghost"
          size="sm"
          aria-label="Aumentar volume"
          title="Aumentar volume"
          disabled={offline || volume >= 100}
          busy={busy === 'setVolume'}
          onClick={() => void run({ command: 'setVolume', volume: Math.min(100, volume + VolumeStep) }, true)}
          icon={<Volume2 className="size-4" />}
        />
      </div>
    </div>
  );
}
