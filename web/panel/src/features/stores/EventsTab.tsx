import { ScrollText } from 'lucide-react';
import { errorMessage } from '../../api/client';
import { useEvents } from '../../api/queries';
import { Badge, Card, CardHeader, EmptyState, ErrorNote, Spinner } from '../../components/ui';
import { eventDetails, eventLabel, eventTone } from '../../lib/events';
import { formatDateTime } from '../../lib/format';

/** Aba "Eventos": o que o agente e o Hub registraram sobre a loja (seção 7.2, eventos e logs recentes). */
export function EventsTab({ storeId }: { storeId: number }) {
  const events = useEvents(storeId);

  return (
    <Card>
      <CardHeader
        title="Eventos recentes"
        subtitle="Os 200 mais recentes. Os logs completos ficam no PC da loja (seção 17)."
      />
      {events.isPending ? (
        <Spinner />
      ) : events.isError ? (
        <div className="p-5">
          <ErrorNote>{errorMessage(events.error)}</ErrorNote>
        </div>
      ) : events.data.length === 0 ? (
        <EmptyState icon={<ScrollText className="size-8" />} title="Nenhum evento registrado" />
      ) : (
        <ol className="divide-y divide-stone-100">
          {events.data.map((event, index) => {
            const details = eventDetails(event.data);
            return (
              <li
                key={`${event.createdAt}-${index}`}
                className="flex flex-wrap items-center gap-x-4 gap-y-1 px-5 py-2.5 text-sm"
              >
                <span className="w-32 shrink-0 text-stone-500 tabular-nums">{formatDateTime(event.createdAt)}</span>
                <Badge tone={eventTone(event.type)} dot>
                  {eventLabel(event.type)}
                </Badge>
                {details && (
                  <span className="min-w-0 flex-1 truncate text-xs text-stone-500" title={details}>
                    {details}
                  </span>
                )}
              </li>
            );
          })}
        </ol>
      )}
    </Card>
  );
}
