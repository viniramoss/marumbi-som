import {
  addOneHour,
  formatAgo,
  formatDays,
  formatDuration,
  formatIn,
  formatLongDuration,
  matchesSearch,
} from './format';

describe('formatDuration', () => {
  it('mostra minutos e segundos, e horas quando passa de uma', () => {
    expect(formatDuration(187_000)).toBe('3:07');
    expect(formatDuration(3_723_000)).toBe('1:02:03');
    expect(formatDuration(0)).toBe('0:00');
  });

  it('mostra traço sem duração conhecida', () => {
    expect(formatDuration(null)).toBe('–:––');
    expect(formatDuration(-1)).toBe('–:––');
  });
});

describe('formatLongDuration', () => {
  it('escreve por extenso', () => {
    expect(formatLongDuration(47 * 60_000)).toBe('47 min');
    expect(formatLongDuration(135 * 60_000)).toBe('2 h 15 min');
    expect(formatLongDuration(24 * 60 * 60_000)).toBe('24 h');
  });
});

describe('formatAgo', () => {
  const now = Date.parse('2026-10-23T13:00:00Z');

  it('diz há quanto tempo', () => {
    expect(formatAgo('2026-10-23T12:59:58Z', now)).toBe('agora');
    expect(formatAgo('2026-10-23T12:59:48Z', now)).toBe('há 12 s');
    expect(formatAgo('2026-10-23T12:55:00Z', now)).toBe('há 5 min');
    expect(formatAgo('2026-10-23T10:00:00Z', now)).toBe('há 3 h');
    expect(formatAgo('2026-10-20T13:00:00Z', now)).toBe('há 3 dias');
    expect(formatAgo(null, now)).toBe('nunca');
  });
});

describe('formatIn', () => {
  const now = Date.parse('2026-10-23T13:00:00Z');

  it('diz quanto falta (próxima oferta prevista)', () => {
    expect(formatIn('2026-10-23T12:59:00Z', now)).toBe('agora');
    expect(formatIn('2026-10-23T13:00:40Z', now)).toBe('em 40 s');
    expect(formatIn('2026-10-23T13:05:00Z', now)).toBe('em 5 min');
    expect(formatIn('2026-10-23T15:00:00Z', now)).toBe('em 2 h');
  });
});

describe('formatDays', () => {
  it('resume os dias da semana', () => {
    expect(formatDays([0, 1, 2, 3, 4, 5, 6])).toBe('Todos os dias');
    expect(formatDays([1, 2, 3, 4, 5])).toBe('Seg a Sex');
    expect(formatDays([6, 0])).toBe('Dom, Sáb');
    expect(formatDays([])).toBe('Nenhum dia');
  });
});

describe('matchesSearch', () => {
  it('ignora maiúsculas e acentos, como a busca do Hub', () => {
    expect(matchesSearch('acai', 'Açaí na Tigela')).toBe(true);
    expect(matchesSearch('EVIDENCIAS', 'Evidências')).toBe(true);
    expect(matchesSearch('xororo', 'Evidências', 'Chitãozinho & Xororó')).toBe(true);
    expect(matchesSearch('samba', 'Evidências', null)).toBe(false);
    expect(matchesSearch('  ', 'qualquer')).toBe(true);
  });
});

describe('addOneHour', () => {
  it('soma 1 hora ao horário da madrugada, virando o dia', () => {
    expect(addOneHour('03:00')).toBe('04:00');
    expect(addOneHour('23:30')).toBe('00:30');
    expect(addOneHour('xx')).toBe('xx');
  });
});
