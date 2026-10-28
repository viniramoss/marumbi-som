import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  TouchSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import {
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { ArrowLeft, GripVertical, ListPlus, Megaphone, Pause, Play, Save, Shuffle, Trash2, X } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useDeletePlaylist, usePlaylist, useSavePlaylist } from '../../api/queries';
import type { PlaylistDetail } from '../../api/types';
import { AppliedStatus } from '../../components/AppliedStatus';
import { ConfirmDialog } from '../../components/Modal';
import { useToast } from '../../components/Toasts';
import { Button, Card, CardHeader, EmptyState, ErrorNote, IconButton, SoonTag, Spinner } from '../../components/ui';
import { usePreview } from '../../hooks/usePreview';
import { useUnsavedChanges } from '../../hooks/useUnsavedChanges';
import { formatDuration, formatLongDuration } from '../../lib/format';
import { AddTracksDialog } from './AddTracksDialog';
import {
  addTracks,
  draftFrom,
  isDirty,
  moveItem,
  removeItem,
  settingsChanged,
  startTimes,
  toItemsInput,
  totalDuration,
  type Draft,
  type DraftItem,
} from './playlistDraft';
import { usageText } from './usage';

export function PlaylistEditorPage() {
  const id = Number(useParams().id);
  const playlist = usePlaylist(id);

  if (playlist.isPending) return <Spinner />;
  if (playlist.isError) return <ErrorNote>{errorMessage(playlist.error)}</ErrorNote>;
  return <Editor key={playlist.data.id} saved={playlist.data} />;
}

function Editor({ saved }: { saved: PlaylistDetail }) {
  const [draft, setDraft] = useState<Draft>(() => draftFrom(saved));
  const [adding, setAdding] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const save = useSavePlaylist(saved.id);
  const remove = useDeletePlaylist();
  const toast = useToast();
  const navigate = useNavigate();
  const preview = usePreview();
  const dirty = isDirty(draft, saved);
  const starts = useMemo(() => startTimes(draft.items), [draft.items]);
  const storeIds = [...new Set(saved.usage.map((u) => u.storeId))];

  // Nada vai para as lojas sem "Salvar e publicar".
  const leaveDialog = useUnsavedChanges(
    dirty,
    'As alterações desta playlist ainda não foram salvas e não chegaram às lojas.',
  );

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 150, tolerance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (over && active.id !== over.id) setDraft((d) => moveItem(d, String(active.id), String(over.id)));
  };

  const publish = async () => {
    if (!draft.name.trim()) {
      toast.error('Dê um nome para a playlist.');
      return;
    }
    try {
      const result = await save.mutateAsync({
        name: draft.name.trim(),
        shuffle: draft.shuffle,
        items: toItemsInput(draft),
        changedSettings: settingsChanged(draft, saved),
      });
      setDraft(draftFrom(result));
      toast.success(
        storeIds.length > 0 ? 'Salva e publicada. As lojas trocam no fim da música atual.' : 'Playlist salva.',
      );
    } catch (error) {
      toast.error(errorMessage(error));
    }
  };

  const confirmDelete = async () => {
    try {
      await remove.mutateAsync(saved.id);
      setDeleting(false);
      toast.success('Playlist removida.');
      void navigate('/playlists', { replace: true });
    } catch (error) {
      setDeleting(false);
      toast.error(errorMessage(error));
    }
  };

  return (
    <>
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <Link to="/playlists" className="inline-flex items-center gap-1 text-sm text-stone-500 hover:text-stone-800">
          <ArrowLeft className="size-4" /> Playlists
        </Link>
      </div>

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0 flex-1">
          <label htmlFor="playlist-name" className="sr-only">
            Nome da playlist
          </label>
          <input
            id="playlist-name"
            value={draft.name}
            maxLength={80}
            onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))}
            className="w-full max-w-xl rounded-lg border-0 bg-transparent px-2 py-1 text-2xl font-semibold tracking-tight ring-0 hover:bg-white focus:bg-white focus:ring-2 focus:ring-brand-600 focus:outline-none"
          />
          <p className="mt-1 px-2 text-sm text-stone-500">
            {draft.items.length} {draft.items.length === 1 ? 'música' : 'músicas'} ·{' '}
            {formatLongDuration(totalDuration(draft.items))}
            {dirty && <span className="ml-2 font-medium text-amber-700">· alterações não salvas</span>}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <label className="inline-flex h-9 cursor-pointer items-center gap-2 rounded-lg bg-white px-3 text-sm font-medium shadow-sm ring-1 ring-stone-300">
            <input
              type="checkbox"
              checked={draft.shuffle}
              onChange={(e) => setDraft((d) => ({ ...d, shuffle: e.target.checked }))}
              className="size-4 accent-brand-700"
            />
            <Shuffle className="size-4 text-stone-500" aria-hidden /> Aleatório
          </label>
          <Button icon={<Trash2 className="size-4" />} variant="danger" onClick={() => setDeleting(true)}>
            Remover
          </Button>
          <Button
            variant="primary"
            icon={<Save className="size-4" />}
            busy={save.isPending}
            disabled={!dirty}
            onClick={() => void publish()}
          >
            Salvar e publicar
          </Button>
        </div>
      </div>

      <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <Card>
          <CardHeader
            title="Músicas"
            subtitle={
              draft.shuffle
                ? 'No aleatório, a ordem abaixo não vale: cada ciclo é sorteado sem repetir.'
                : 'Arraste pela alça para mudar a ordem.'
            }
            actions={
              <Button variant="primary" icon={<ListPlus className="size-4" />} onClick={() => setAdding(true)}>
                Adicionar músicas
              </Button>
            }
          />
          {draft.items.length === 0 ? (
            <EmptyState title="Playlist vazia">Use "Adicionar músicas" para escolher da biblioteca.</EmptyState>
          ) : (
            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
              <SortableContext items={draft.items.map((i) => i.key)} strategy={verticalListSortingStrategy}>
                <ol className="divide-y divide-stone-100" aria-label="Músicas da playlist">
                  {draft.items.map((item, index) => (
                    <SortableRow
                      key={item.key}
                      item={item}
                      index={index}
                      start={starts[index]!}
                      playing={preview.playing === item.hash}
                      onPreview={() => preview.toggle(item.hash)}
                      onRemove={() => setDraft((d) => removeItem(d, item.key))}
                    />
                  ))}
                </ol>
              </SortableContext>
            </DndContext>
          )}
        </Card>

        <div className="space-y-6">
          <Card>
            <CardHeader title="Lojas que tocam" subtitle="Situação depois do último salvamento." />
            <div className="space-y-4 px-5 py-4">
              <AppliedStatus storeIds={storeIds} />
              {saved.usage.length > 0 && (
                <ul className="space-y-1 border-t border-stone-100 pt-3 text-xs text-stone-500">
                  {saved.usage.map((usage, index) => (
                    <li key={index}>
                      {usage.storeName}: {usageText(usage)}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </Card>
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
              Esta coluna vai receber as ofertas: arrastar para o espaço entre duas músicas ou para cima de uma música
              (em um tempo marcado). Chega com a tela de Ofertas.
            </p>
          </Card>
        </div>
      </div>

      {adding && (
        <AddTracksDialog
          existing={new Set(draft.items.map((i) => i.hash))}
          onClose={() => setAdding(false)}
          onAdd={(tracks) => {
            setDraft((d) => addTracks(d, tracks));
            setAdding(false);
          }}
        />
      )}
      {deleting && (
        <ConfirmDialog
          title="Remover a playlist?"
          confirmLabel="Remover"
          busy={remove.isPending}
          onCancel={() => setDeleting(false)}
          onConfirm={() => void confirmDelete()}
        >
          <p>
            A playlist <strong>{saved.name}</strong> será apagada. As músicas continuam na biblioteca.
          </p>
          {saved.usage.length > 0 && (
            <p className="rounded-lg bg-amber-50 px-3 py-2 text-amber-900 ring-1 ring-amber-200">
              Ela está na agenda de {[...new Set(saved.usage.map((u) => u.storeName))].join(', ')}. Tire da agenda antes
              de remover.
            </p>
          )}
        </ConfirmDialog>
      )}
      {leaveDialog}
    </>
  );
}

function SortableRow({
  item,
  index,
  start,
  playing,
  onPreview,
  onRemove,
}: {
  item: DraftItem;
  index: number;
  start: number;
  playing: boolean;
  onPreview: () => void;
  onRemove: () => void;
}) {
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({
    id: item.key,
  });
  return (
    <li
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={`flex items-center gap-2 bg-white px-3 py-2 text-sm sm:gap-3 sm:px-5 ${isDragging ? 'relative z-10 shadow-lg ring-1 ring-stone-200' : ''}`}
    >
      <button
        ref={setActivatorNodeRef}
        type="button"
        aria-label={`Mover ${item.title}`}
        className="cursor-grab touch-none rounded p-1 text-stone-400 hover:bg-stone-100 hover:text-stone-700 active:cursor-grabbing"
        {...attributes}
        {...listeners}
      >
        <GripVertical className="size-4" />
      </button>
      <span className="w-7 text-right text-xs text-stone-400 tabular-nums">{index + 1}</span>
      <span className="hidden w-16 text-xs text-stone-500 tabular-nums sm:block" title="Começa em (tempo acumulado)">
        {formatDuration(start)}
      </span>
      <IconButton label={playing ? 'Parar pré-escuta' : 'Ouvir'} onClick={onPreview} className="size-8">
        {playing ? <Pause className="size-4 text-brand-700" /> : <Play className="size-4" />}
      </IconButton>
      <div className="min-w-0 flex-1">
        <p className="truncate font-medium text-stone-900">{item.title}</p>
        <p className="truncate text-xs text-stone-500">{item.artist ?? 'Artista desconhecido'}</p>
      </div>
      {item.id === null && (
        <span className="rounded bg-sky-50 px-1.5 py-0.5 text-[11px] font-medium text-sky-700">nova</span>
      )}
      <span className="w-12 text-right text-stone-600 tabular-nums">{formatDuration(item.durationMs)}</span>
      <IconButton label={`Tirar ${item.title} da playlist`} onClick={onRemove} className="size-8">
        <X className="size-4" />
      </IconButton>
    </li>
  );
}
