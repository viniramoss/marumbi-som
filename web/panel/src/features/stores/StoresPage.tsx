import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { Plus, Store } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useCreateStore, useStores } from '../../api/queries';
import type { StoreSummary } from '../../api/types';
import { Modal } from '../../components/Modal';
import { Badge, Button, Card, EmptyState, ErrorNote, Field, Input, PageHeader, Spinner } from '../../components/ui';
import { useNow } from '../../hooks/useNow';
import { formatAgo } from '../../lib/format';
import { bluetoothStatus, playerStatus, syncStatus } from '../../lib/status';

export function StoresPage() {
  const stores = useStores();
  const [creating, setCreating] = useState(false);
  const now = useNow(5000);

  return (
    <>
      <PageHeader
        title="Lojas"
        description="Agenda, controle ao vivo, histórico e vinculação de cada loja."
        actions={
          <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setCreating(true)}>
            Nova loja
          </Button>
        }
      />
      <Card>
        {stores.isPending ? (
          <Spinner />
        ) : stores.isError ? (
          <div className="p-5">
            <ErrorNote>{errorMessage(stores.error)}</ErrorNote>
          </div>
        ) : stores.data.length === 0 ? (
          <EmptyState icon={<Store className="size-10" />} title="Nenhuma loja cadastrada">
            Cadastre a loja, gere o código de vinculação e informe esse código na configuração local do PC da loja.
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[760px] text-sm">
              <thead>
                <tr className="border-b border-stone-100 text-left text-xs font-medium tracking-wide text-stone-500 uppercase">
                  <th className="px-5 py-2.5">Loja</th>
                  <th className="w-36 py-2.5 pr-4">Situação</th>
                  <th className="py-2.5 pr-4">Playlist agora</th>
                  <th className="py-2.5 pr-4">Caixa</th>
                  <th className="py-2.5 pr-4">Configuração</th>
                  <th className="w-40 py-2.5 pr-5">Último sinal</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-stone-100">
                {stores.data.map((store) => (
                  <StoreRow key={store.id} store={store} now={now} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
      {creating && <CreateStoreDialog onClose={() => setCreating(false)} />}
    </>
  );
}

function StoreRow({ store, now }: { store: StoreSummary; now: number }) {
  const status = playerStatus(store);
  const bluetooth = bluetoothStatus(store);
  const sync = syncStatus(store);
  return (
    <tr className="hover:bg-stone-50">
      <td className="px-5 py-3">
        <Link to={`/lojas/${store.id}`} className="font-medium text-stone-900 hover:text-brand-800">
          {store.name}
        </Link>
        <p className="text-xs text-stone-400">{store.slug}</p>
      </td>
      <td className="py-3 pr-4">
        <div className="flex flex-col items-start gap-1">
          <Badge tone={status.tone} dot>
            {status.label}
          </Badge>
          {store.alerts.length > 0 && (
            <Badge tone="red">{store.alerts.length === 1 ? '1 alerta' : `${store.alerts.length} alertas`}</Badge>
          )}
        </div>
      </td>
      <td className="py-3 pr-4 text-stone-700">
        {store.playlistName ??
          (store.hasSchedule ? (
            <span className="text-stone-400">{store.open ? 'Nenhuma' : 'Fora do horário'}</span>
          ) : (
            <span className="text-amber-700">Sem agenda</span>
          ))}
      </td>
      <td className="py-3 pr-4 text-stone-700">{store.enrolled ? bluetooth.label : '–'}</td>
      <td className="py-3 pr-4 text-stone-700">{sync.label}</td>
      <td className="py-3 pr-5 text-stone-500">
        {!store.enrolled ? 'Nunca conectou' : store.online ? 'Conectada agora' : formatAgo(store.lastSeenAt, now)}
      </td>
    </tr>
  );
}

function CreateStoreDialog({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState('');
  const create = useCreateStore();
  const navigate = useNavigate();

  const submit = async () => {
    try {
      const store = await create.mutateAsync(name.trim());
      onClose();
      void navigate(`/lojas/${store.status.id}`);
    } catch {
      // O erro aparece no próprio diálogo.
    }
  };

  return (
    <Modal
      title="Nova loja"
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancelar</Button>
          <Button variant="primary" busy={create.isPending} disabled={!name.trim()} onClick={() => void submit()}>
            Cadastrar
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
        <Field label="Nome" hint="Ex.: Marumbi Centro. Depois de cadastrar, gere o código para vincular o PC da loja.">
          <Input value={name} onChange={(e) => setName(e.target.value)} maxLength={80} autoFocus />
        </Field>
        {create.isError && <ErrorNote>{errorMessage(create.error)}</ErrorNote>}
      </form>
    </Modal>
  );
}
