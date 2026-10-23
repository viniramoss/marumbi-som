import { TriangleAlert } from 'lucide-react';
import type { StoreAlert } from '../../api/types';
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
