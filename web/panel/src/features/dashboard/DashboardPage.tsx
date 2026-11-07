import { Link } from 'react-router';
import { Store } from 'lucide-react';
import { useStores } from '../../api/queries';
import { Button, Card, EmptyState, ErrorNote, PageHeader, Spinner } from '../../components/ui';
import { errorMessage } from '../../api/client';
import { AgentUpdateNotice } from './AgentUpdateNotice';
import { StoreCard } from './StoreCard';

export function DashboardPage() {
  const stores = useStores();

  if (stores.isPending) return <Spinner />;
  if (stores.isError) return <ErrorNote>{errorMessage(stores.error)}</ErrorNote>;

  const list = stores.data;
  const playing = list.filter((s) => s.online && s.player === 'playing').length;
  const withAlerts = list.filter((s) => s.alerts.length > 0).length;
  const summary =
    list.length === 0
      ? 'Nenhuma loja cadastrada.'
      : `${list.length} ${list.length === 1 ? 'loja' : 'lojas'} · ${playing} tocando` +
        (withAlerts > 0 ? ` · ${withAlerts} com alerta` : ' · tudo certo');

  return (
    <>
      <PageHeader title="Início" description={summary} />
      <AgentUpdateNotice stores={list} />
      {list.length === 0 ? (
        <Card>
          <EmptyState icon={<Store className="size-10" />} title="Cadastre a primeira loja">
            <p>Crie a loja, monte uma playlist e defina o horário. Depois é só vincular o PC da loja com o código.</p>
            <Link to="/lojas" className="mt-4 inline-block">
              <Button variant="primary">Ir para Lojas</Button>
            </Link>
          </EmptyState>
        </Card>
      ) : (
        <div className="grid gap-5 lg:grid-cols-2 2xl:grid-cols-3">
          {[...list]
            .sort((a, b) => b.alerts.length - a.alerts.length || a.name.localeCompare(b.name, 'pt-BR'))
            .map((store) => (
              <StoreCard key={store.id} store={store} />
            ))}
        </div>
      )}
    </>
  );
}
