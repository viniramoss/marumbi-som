import type { OfferDetail, OfferInput, OfferRule, OfferState, OfferSummary, StoreSummary } from '../../api/types';
import { formatDays } from '../../lib/format';
import type { Tone } from '../../lib/status';

/** Situação calculada pelo Hub (D26 e), com o aviso de vencimento nas próximas 24 h. */
export function offerStatus(offer: Pick<OfferSummary, 'status' | 'expiresSoon'>): { label: string; tone: Tone } {
  const look: Record<OfferState, { label: string; tone: Tone }> = {
    live: { label: 'No ar', tone: 'green' },
    scheduled: { label: 'Agendada', tone: 'blue' },
    disabled: { label: 'Desativada', tone: 'gray' },
    expired: { label: 'Vencida', tone: 'gray' },
  };
  return look[offer.status];
}

/** Regra como o painel edita. `original` guarda regra criada fora do painel (ex.: "a cada N músicas"), que só sai. */
export interface RuleDraft {
  key: string;
  kind: string;
  minutes: number;
  time: string;
  days: number[];
  playlistIds: number[];
  original?: OfferRule;
}

export interface OfferDraft {
  name: string;
  audioHash: string | null;
  /** Valor de `<input type="datetime-local">` (horário do navegador). */
  validFrom: string;
  validUntil: string;
  duckLevel: number;
  enabled: boolean;
  storeIds: number[];
  rules: RuleDraft[];
}

/** Regras que o painel cria (D24: sempre durante a música, com ducking). */
export const editableKinds = ['everyMinutes', 'clock'] as const;

let counter = 0;
const ruleKey = () => `regra-${++counter}`;

const pad = (n: number) => String(n).padStart(2, '0');

/** Data para `<input type="datetime-local">`, no horário do navegador. */
export function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function fromLocalInput(value: string): string | null {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

export function newRule(kind: (typeof editableKinds)[number]): RuleDraft {
  return { key: ruleKey(), kind, minutes: 30, time: '12:00', days: [0, 1, 2, 3, 4, 5, 6], playlistIds: [] };
}

/** Oferta nova: vale de agora até o fim do sétimo dia, em todas as lojas, a cada 30 minutos. */
export function newOfferDraft(now: Date, storeIds: number[]): OfferDraft {
  const until = new Date(now);
  until.setDate(until.getDate() + 7);
  until.setHours(23, 59, 0, 0);
  return {
    name: '',
    audioHash: null,
    validFrom: toLocalInput(now),
    validUntil: toLocalInput(until),
    duckLevel: 30,
    enabled: true,
    storeIds,
    rules: [newRule('everyMinutes')],
  };
}

export function draftFromOffer(detail: OfferDetail): OfferDraft {
  const { offer } = detail;
  return {
    name: offer.name,
    audioHash: offer.audioHash,
    validFrom: toLocalInput(new Date(offer.validFrom)),
    validUntil: toLocalInput(new Date(offer.validUntil)),
    duckLevel: offer.duckLevel,
    enabled: offer.enabled,
    storeIds: [...offer.storeIds].sort((a, b) => a - b),
    rules: detail.rules.map((rule) => ({
      key: ruleKey(),
      kind: rule.kind,
      minutes: rule.minutes ?? 30,
      time: rule.time ?? '12:00',
      days: rule.days ?? [],
      playlistIds: rule.playlistIds ?? [],
      original: (editableKinds as readonly string[]).includes(rule.kind) ? undefined : rule,
    })),
  };
}

function toRule(rule: RuleDraft): OfferRule {
  if (rule.original) return rule.original;
  const playlistIds = rule.playlistIds.length > 0 ? [...rule.playlistIds].sort((a, b) => a - b) : null;
  return rule.kind === 'clock'
    ? {
        kind: 'clock',
        id: null,
        mode: 'during',
        minutes: null,
        time: rule.time,
        days: [...rule.days].sort((a, b) => a - b),
        n: null,
        playlistIds,
      }
    : {
        kind: 'everyMinutes',
        id: null,
        mode: 'during',
        minutes: rule.minutes,
        time: null,
        days: null,
        n: null,
        playlistIds,
      };
}

export function toOfferInput(draft: OfferDraft): OfferInput {
  return {
    name: draft.name.trim(),
    audioHash: draft.audioHash,
    validFrom: fromLocalInput(draft.validFrom),
    validUntil: fromLocalInput(draft.validUntil),
    duckLevel: draft.duckLevel,
    enabled: draft.enabled,
    storeIds: [...draft.storeIds].sort((a, b) => a - b),
    rules: draft.rules.map(toRule),
  };
}

/** Mudou algo em relação ao que está salvo (as chaves das regras não contam). */
export function isOfferDirty(draft: OfferDraft, saved: OfferDraft): boolean {
  return JSON.stringify(toOfferInput(draft)) !== JSON.stringify(toOfferInput(saved));
}

/** Conferência antes de enviar; o Hub confere de novo e devolve o motivo em português. */
export function draftProblem(draft: OfferDraft): string | null {
  if (!draft.audioHash) return 'Envie o áudio da oferta e espere o processamento terminar.';
  if (!draft.name.trim()) return 'Dê um nome para a oferta.';
  const from = fromLocalInput(draft.validFrom);
  const until = fromLocalInput(draft.validUntil);
  if (!from || !until) return 'Preencha o início e o fim da validade.';
  if (Date.parse(until) <= Date.parse(from)) return 'O fim da validade precisa ser depois do início.';
  for (const rule of draft.rules) {
    if (rule.original) continue;
    if (rule.kind === 'everyMinutes' && !(rule.minutes >= 1 && rule.minutes <= 1440)) {
      return '"A cada X minutos" precisa de 1 a 1440 minutos.';
    }
    if (rule.kind === 'clock' && !/^([01]\d|2[0-3]):[0-5]\d$/.test(rule.time)) return '"Horário fixo" precisa da hora.';
    if (rule.kind === 'clock' && rule.days.length === 0) {
      return '"Horário fixo" precisa de pelo menos um dia da semana.';
    }
  }
  return null;
}

/** "A cada 15 min", "Às 12:30, Seg a Sex", com "só em Manhã" quando restrita a playlists. */
export function ruleText(rule: OfferRule, playlistName: (id: number) => string | undefined): string {
  let text: string;
  switch (rule.kind) {
    case 'everyMinutes':
      text = `A cada ${rule.minutes ?? '?'} min`;
      break;
    case 'clock':
      text = `Às ${rule.time ?? '?'}, ${formatDays(rule.days ?? [])}`;
      break;
    case 'everyTracks':
      text = `A cada ${rule.n ?? '?'} músicas (entre músicas)`;
      break;
    default:
      text = rule.kind;
  }
  if (rule.playlistIds && rule.playlistIds.length > 0) {
    text += ` · só em ${rule.playlistIds.map((id) => playlistName(id) ?? `playlist ${id}`).join(', ')}`;
  }
  return text;
}

/** "vence em 5 h", "vence em 40 min". */
export function expiresIn(validUntil: string, now: number): string {
  const minutes = Math.max(0, Math.round((Date.parse(validUntil) - now) / 60_000));
  if (minutes < 60) return `vence em ${minutes} min`;
  return `vence em ${Math.round(minutes / 60)} h`;
}

/**
 * Aviso para "a cada X minutos" menor que o intervalo mínimo entre ofertas das lojas escolhidas: o intervalo vale para
 * todas as ofertas juntas e vence a regra, então na prática a oferta toca com menos frequência (achado no piloto).
 */
export function gapWarning(
  minutes: number,
  stores: readonly Pick<StoreSummary, 'name' | 'minOfferGapS'>[],
): string | null {
  if (!Number.isFinite(minutes) || minutes <= 0) return null;
  const slower = stores
    .map((s) => ({ name: s.name, gap: Math.round(s.minOfferGapS / 60) }))
    .filter((s) => s.gap > minutes);
  if (slower.length === 0) return null;
  const names = (list: string[]) =>
    list.length === 1 ? list[0]! : `${list.slice(0, -1).join(', ')} e ${list[list.length - 1]!}`;
  const where = 'contando todas as ofertas (Loja → Ajustes → Intervalo mínimo entre ofertas)';
  const gap = slower[0]!.gap;
  if (slower.every((s) => s.gap === gap)) {
    const place = stores.length > 1 ? ` em ${names(slower.map((s) => s.name))}` : '';
    const who = slower.length === 1 ? 'a loja toca' : 'as lojas tocam';
    return `Na prática toca a cada ${gap} min${place}: ${who} no máximo 1 oferta a cada ${gap} min, ${where}.`;
  }
  const each = slower.map((s) => `${s.name} toca no máximo 1 oferta a cada ${s.gap} min`);
  return `Na prática toca com menos frequência: ${names(each)}, ${where}.`;
}
