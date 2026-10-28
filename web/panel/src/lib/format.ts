const pad = (n: number) => String(n).padStart(2, '0');

/** 3:07, 1:02:03. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return '–:––';
  const total = Math.floor(ms / 1000);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

/** Duração longa por extenso: "2 h 15 min", "47 min". */
export function formatLongDuration(ms: number): string {
  const minutes = Math.round(ms / 60_000);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** "agora", "há 12 s", "há 5 min", "há 3 h", "há 2 dias". */
export function formatAgo(iso: string | null | undefined, now: number): string {
  if (!iso) return 'nunca';
  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000));
  if (seconds < 5) return 'agora';
  if (seconds < 60) return `há ${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `há ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `há ${hours} h`;
  return `há ${Math.floor(hours / 24)} dias`;
}

/** Para algo previsto: "agora", "em 40 s", "em 5 min", "em 2 h". */
export function formatIn(iso: string, now: number): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000);
  if (seconds < 5) return 'agora';
  if (seconds < 60) return `em ${seconds} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `em ${minutes} min`;
  return `em ${Math.round(minutes / 60)} h`;
}

const dateTime = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
const time = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });
const timeSeconds = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const weekdayDate = new Intl.DateTimeFormat('pt-BR', { weekday: 'short', day: '2-digit', month: '2-digit' });

export const formatDateTime = (iso: string) => dateTime.format(new Date(iso));
export const formatTime = (iso: string) => time.format(new Date(iso));
export const formatTimeSeconds = (iso: string) => timeSeconds.format(new Date(iso));
export const formatWeekdayDate = (iso: string) => weekdayDate.format(new Date(iso));

/** "03:00" → "04:00": fim da janela de 1 hora da atualização do Hub de madrugada (HubUpdates.AutoWindow). */
export function addOneHour(time: string): string {
  const [hours, minutes] = time.split(':').map(Number);
  if (hours === undefined || minutes === undefined || Number.isNaN(hours) || Number.isNaN(minutes)) return time;
  return `${String((hours + 1) % 24).padStart(2, '0')}:${String(minutes).padStart(2, '0')}`;
}

/** 1,5 GB, 820 MB. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '–';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const digits = value < 10 && unit > 0 ? 1 : 0;
  return `${value.toLocaleString('pt-BR', { maximumFractionDigits: digits, minimumFractionDigits: digits })} ${units[unit]}`;
}

export const formatMegabytes = (mb: number | null | undefined) =>
  mb === null || mb === undefined ? '–' : formatBytes(mb * 1024 * 1024);

/** Busca sem diferenciar maiúsculas nem acentos ("acai" acha "Açaí"), como a busca do Hub. */
export function normalizeSearch(text: string): string {
  return text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .trim();
}

export function matchesSearch(query: string, ...fields: (string | null | undefined)[]): boolean {
  const q = normalizeSearch(query);
  if (!q) return true;
  return fields.some((f) => f && normalizeSearch(f).includes(q));
}

export const weekdayShort = ['Dom', 'Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb'] as const;
export const weekdayLetter = ['D', 'S', 'T', 'Q', 'Q', 'S', 'S'] as const;

/** "Seg a Sex", "Todos os dias", "Sáb, Dom". */
export function formatDays(days: readonly number[]): string {
  const sorted = [...new Set(days)].sort((a, b) => a - b);
  if (sorted.length === 7) return 'Todos os dias';
  if (sorted.length === 0) return 'Nenhum dia';
  const isRange = sorted.length >= 3 && sorted.every((d, i) => i === 0 || d === sorted[i - 1]! + 1);
  if (isRange) return `${weekdayShort[sorted[0]!]} a ${weekdayShort[sorted[sorted.length - 1]!]}`;
  return sorted.map((d) => weekdayShort[d]).join(', ');
}
