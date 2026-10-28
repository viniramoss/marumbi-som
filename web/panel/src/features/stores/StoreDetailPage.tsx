import { Link, useParams, useSearchParams } from 'react-router';
import { ArrowLeft } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useStore } from '../../api/queries';
import { Badge, ErrorNote, Spinner, Tabs } from '../../components/ui';
import { appliedStatus, playerStatus } from '../../lib/status';
import { EventsTab } from './EventsTab';
import { HistoryTab } from './HistoryTab';
import { LiveTab } from './LiveTab';
import { ScheduleTab } from './ScheduleTab';
import { StoreSettingsTab } from './StoreSettingsTab';

const tabs = [
  { id: 'ao-vivo', label: 'Ao vivo' },
  { id: 'agenda', label: 'Agenda' },
  { id: 'historico', label: 'Histórico' },
  { id: 'eventos', label: 'Eventos' },
  { id: 'ajustes', label: 'Ajustes' },
] as const;

type TabId = (typeof tabs)[number]['id'];

const isTab = (value: string | null): value is TabId => tabs.some((t) => t.id === value);

/** Loja (detalhe), seção 7.2. A aba fica no endereço (`?aba=agenda`) para poder voltar e compartilhar. */
export function StoreDetailPage() {
  const id = Number(useParams().id);
  const store = useStore(id);
  const [params, setParams] = useSearchParams();
  const requested = params.get('aba');
  const tab: TabId = isTab(requested) ? requested : 'ao-vivo';

  if (store.isPending) return <Spinner />;
  if (store.isError) {
    return (
      <div className="space-y-4">
        <BackLink />
        <ErrorNote>{errorMessage(store.error)}</ErrorNote>
      </div>
    );
  }

  const detail = store.data;
  const status = playerStatus(detail.status);
  const applied = appliedStatus(detail.status);

  return (
    <>
      <BackLink />
      <header className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <div className="min-w-0">
          <h1 className="truncate text-2xl font-semibold tracking-tight text-stone-900">{detail.status.name}</h1>
          <p className="mt-1 text-sm text-stone-500">
            {detail.status.slug} · fuso {detail.timezone}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={status.tone} dot>
            {status.label}
          </Badge>
          {detail.status.enrolled && (
            <Badge tone={applied.tone} dot>
              {applied.label}
            </Badge>
          )}
        </div>
      </header>
      <div className="mb-6">
        <Tabs tabs={tabs} value={tab} onChange={(next) => setParams(next === 'ao-vivo' ? {} : { aba: next })} />
      </div>
      {tab === 'ao-vivo' && <LiveTab detail={detail} />}
      {tab === 'agenda' && <ScheduleTab detail={detail} />}
      {tab === 'historico' && <HistoryTab storeId={id} />}
      {tab === 'eventos' && <EventsTab storeId={id} />}
      {tab === 'ajustes' && <StoreSettingsTab key={id} detail={detail} />}
    </>
  );
}

function BackLink() {
  return (
    <div className="mb-4">
      <Link to="/lojas" className="inline-flex items-center gap-1 text-sm text-stone-500 hover:text-stone-800">
        <ArrowLeft className="size-4" /> Lojas
      </Link>
    </div>
  );
}
