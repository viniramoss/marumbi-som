/** Nome legível dos eventos que o agente e o Hub registram. */
const labels: Record<string, string> = {
  'bluetooth.lost': 'Caixa de som desconectou',
  'bluetooth.connected': 'Caixa de som conectada',
  'bluetooth.radioOff': 'Bluetooth do PC desligado',
  'bluetooth.recovery': 'Tentativa de recuperar a caixa',
  'player.restarted': 'Player reiniciado',
  'agent.started': 'Agente iniciado',
  'agent.restarted': 'Agente reiniciou',
  'agent.enrolled': 'Loja vinculada ao Hub',
};

export function eventLabel(type: string): string {
  return labels[type] ?? type;
}

export function eventTone(type: string): 'red' | 'green' | 'amber' | 'gray' {
  if (type === 'bluetooth.lost' || type === 'bluetooth.radioOff' || type === 'agent.restarted') return 'red';
  if (type === 'bluetooth.connected' || type === 'agent.enrolled') return 'green';
  if (type === 'bluetooth.recovery' || type === 'player.restarted') return 'amber';
  return 'gray';
}

export function eventDetails(data: Record<string, unknown> | null): string {
  if (!data) return '';
  return Object.entries(data)
    .map(([key, value]) => `${key}: ${typeof value === 'object' ? JSON.stringify(value) : String(value)}`)
    .join(' · ');
}
