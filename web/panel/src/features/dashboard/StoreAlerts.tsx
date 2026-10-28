import { Link } from 'react-router';
import { CalendarX, TriangleAlert } from 'lucide-react';
import type { StoreAlert, StoreSummary } from '../../api/types';
import { formatTime } from '../../lib/format';

/** Alertas da seção 6.6, que pela D21 aparecem no painel em vez de irem para o Telegram. */
export function StoreAlerts({ alerts }: { alerts: StoreAlert[] }) {
  if (alerts.length === 0) return null;
  return (
    <ul className="space-y-1.5">
      {alerts.map((alert) => (
        <li
          key={alert.code}
          className="flex items-start gap-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800 ring-1 ring-red-200"
        >
          <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>
            {alert.message}
            {alert.since && <span className="text-red-600"> Desde {formatTime(alert.since)}.</span>}
          </span>
        </li>
      ))}
    </ul>
  );
}

/** Loja vinculada sem nenhuma faixa na agenda: não toca nada, e quem acabou de vincular não espera isso. */
export function NoScheduleNotice({ store }: { store: StoreSummary }) {
  if (!store.enrolled || store.hasSchedule) return null;
  return (
    <p className="flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 ring-1 ring-amber-200">
      <CalendarX className="mt-0.5 size-4 shrink-0" aria-hidden />
      <span>
        Sem agenda: a loja não toca nada.{' '}
        <Link to={`/lojas/${store.id}?aba=agenda`} className="font-medium underline">
          Montar a agenda
        </Link>
      </span>
    </p>
  );
}
