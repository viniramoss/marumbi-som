import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ListMusic, Plus, Shuffle } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useCreatePlaylist, usePlaylists } from '../../api/queries';
import { Modal } from '../../components/Modal';
import { Button, Card, EmptyState, ErrorNote, Field, Input, PageHeader, Spinner } from '../../components/ui';
import { formatLongDuration } from '../../lib/format';
import { usageText } from './usage';

export function PlaylistsPage() {
  const playlists = usePlaylists();
  const [creating, setCreating] = useState(false);

  return (
    <>
      <PageHeader
        title="Playlists"
        description="Salvar uma playlist publica na hora para as lojas que a tocam."
        actions={
          <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setCreating(true)}>
            Nova playlist
          </Button>
        }
      />
      <Card>
        {playlists.isPending ? (
          <Spinner />
        ) : playlists.isError ? (
          <div className="p-5">
            <ErrorNote>{errorMessage(playlists.error)}</ErrorNote>
          </div>
        ) : playlists.data.length === 0 ? (
          <EmptyState icon={<ListMusic className="size-10" />} title="Nenhuma playlist">
            Crie uma playlist e coloque as músicas da biblioteca na ordem que quiser.
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-sm">
              <thead>
                <tr className="border-b border-stone-100 text-left text-xs font-medium tracking-wide text-stone-500 uppercase">
                  <th className="px-5 py-2.5">Playlist</th>
                  <th className="w-24 py-2.5 pr-4 text-right">Músicas</th>
                  <th className="w-32 py-2.5 pr-4 text-right">Duração</th>
                  <th className="py-2.5 pr-5">Lojas e horários</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-stone-100">
                {playlists.data.map((playlist) => (
                  <tr key={playlist.id} className="hover:bg-stone-50">
                    <td className="px-5 py-3">
                      <Link
                        to={`/playlists/${playlist.id}`}
                        className="inline-flex items-center gap-2 font-medium text-stone-900 hover:text-brand-800"
                      >
                        {playlist.name}
                        {playlist.shuffle && <Shuffle className="size-3.5 text-stone-400" aria-label="Aleatória" />}
                      </Link>
                    </td>
                    <td className="py-3 pr-4 text-right tabular-nums">{playlist.itemCount}</td>
                    <td className="py-3 pr-4 text-right text-stone-600">{formatLongDuration(playlist.durationMs)}</td>
                    <td className="py-3 pr-5">
                      {playlist.usage.length === 0 ? (
                        <span className="text-stone-400">Fora da agenda</span>
                      ) : (
                        <ul className="space-y-0.5">
                          {playlist.usage.map((usage, index) => (
                            <li key={index} className="text-stone-700">
                              <span className="font-medium">{usage.storeName}</span>{' '}
                              <span className="text-stone-500">· {usageText(usage)}</span>
                            </li>
                          ))}
                        </ul>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
      {creating && <CreatePlaylistDialog onClose={() => setCreating(false)} />}
    </>
  );
}

function CreatePlaylistDialog({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState('');
  const create = useCreatePlaylist();
  const navigate = useNavigate();

  const submit = async () => {
    try {
      const playlist = await create.mutateAsync(name.trim());
      onClose();
      void navigate(`/playlists/${playlist.id}`);
    } catch {
      // O erro aparece no próprio diálogo.
    }
  };

  return (
    <Modal
      title="Nova playlist"
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" busy={create.isPending} disabled={!name.trim()} onClick={() => void submit()}>
            Criar
          </Button>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) void submit();
        }}
        className="space-y-3"
      >
        <Field label="Nome" hint="Ex.: Manhã, Sertanejo, Fim de semana">
          <Input value={name} onChange={(e) => setName(e.target.value)} maxLength={80} autoFocus />
        </Field>
        {create.isError && <ErrorNote>{errorMessage(create.error)}</ErrorNote>}
      </form>
    </Modal>
  );
}
