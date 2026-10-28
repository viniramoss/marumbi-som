'use strict';

const $ = (id) => document.getElementById(id);
let statusTimer;

async function api(path, options = {}) {
  const init = { credentials: 'same-origin', headers: { 'X-MarumbiSom': '1' }, ...options };
  if (options.body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(options.body);
  }
  const response = await fetch(path, init);
  const type = response.headers.get('Content-Type') || '';
  const data = type.includes('json') ? await response.json() : await response.text();
  if (response.status === 401 && path !== '/api/login' && path !== '/api/password') {
    showLogin('Sessão expirada. Entre de novo.');
  }
  if (!response.ok) {
    throw new Error((data && data.error) || `Erro ${response.status}`);
  }
  return data;
}

function setMsg(id, text, ok) {
  const el = $(id);
  el.textContent = text || '';
  el.className = 'msg' + (text ? (ok ? ' ok' : ' bad') : '');
}

function showLogin(message) {
  clearInterval(statusTimer);
  $('app').hidden = true;
  $('login').hidden = false;
  setMsg('login-msg', message, false);
  $('password').focus();
}

async function showApp() {
  $('login').hidden = true;
  $('app').hidden = false;
  await Promise.all([loadStatus(), loadDevices(), loadLogs()]);
  clearInterval(statusTimer);
  statusTimer = setInterval(loadStatus, 3000);
}

function time(seconds) {
  if (seconds == null) return '–';
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

function uptime(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

const playerText = { playing: 'Tocando', paused: 'Pausado', stopped: 'Parado' };
const bluetoothText = { connected: 'Conectada', connecting: 'Conectando…', disconnected: 'Desconectada', radioOff: 'Bluetooth desligado' };

async function loadStatus() {
  let s;
  try {
    s = await api('/api/status');
  } catch {
    return;
  }
  const p = s.player;
  // Entre duas músicas a oferta toca sozinha: não há faixa, mas o som não está parado.
  const betweenSpot = p.status === 'stopped' && p.spot;
  const musicLabel = betweenSpot ? 'Tocando oferta' : playerText[p.status] || p.status;
  const version = (s.agentVersion || '?').replace(/\+([0-9a-f]{7})[0-9a-f]+$/, '+$1');
  const rows = [
    ['Música', `<span class="${p.status === 'playing' || betweenSpot ? 'ok' : 'bad'}">${musicLabel}</span>${p.reason && !betweenSpot ? ' · ' + escapeHtml(p.reason) : ''}`],
    ['Faixa', p.title ? `${escapeHtml(p.artist ? p.artist + ' – ' + p.title : p.title)} (${time(p.positionS)} / ${time(p.durationS)})${p.crossfading ? ' · mixando' : ''}` : '–'],
    ['Playlist', escapeHtml(p.playlist || '–')],
    ['Oferta', escapeHtml(p.spot || '–')],
    ['Volume', String(p.volume)],
    ['Caixa', `<span class="${s.bluetooth === 'connected' ? 'ok' : 'bad'}">${bluetoothText[s.bluetooth] || s.bluetooth}</span>${s.speakerAddress ? ' · ' + s.speakerAddress : ' · nenhuma escolhida'}`],
    ['Loja', escapeHtml(s.storeId || '–')],
    ['Configuração', s.manifestVersion != null ? `versão ${s.manifestVersion}` : 'nenhuma'],
    ['Agente', `${escapeHtml(version)} · ligado há ${uptime(s.uptimeS)}`],
  ];
  $('status').innerHTML = rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
}

async function loadDevices() {
  const list = $('devices');
  try {
    const devices = await api('/api/devices');
    if (devices.length === 0) {
      list.innerHTML = '<li><label>Nenhum dispositivo Bluetooth pareado.</label></li>';
      return;
    }
    list.innerHTML = devices.map((d) => `
      <li><label>
        <input type="radio" name="speaker" value="${d.address}" ${d.selected ? 'checked' : ''}>
        <span>${escapeHtml(d.name)}<br><small>${d.address}${d.connected ? ' · conectada' : ''}${d.selected ? ' · em uso' : ''}</small></span>
      </label></li>`).join('');
  } catch (e) {
    list.innerHTML = `<li><label class="bad">${escapeHtml(e.message)}</label></li>`;
  }
}

async function loadLogs() {
  try {
    $('logs').textContent = await api('/api/logs?lines=200');
    $('logs').scrollTop = $('logs').scrollHeight;
  } catch (e) {
    $('logs').textContent = e.message;
  }
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    await api('/api/login', { method: 'POST', body: { password: $('password').value } });
    $('password').value = '';
    setMsg('login-msg', '');
    await showApp();
  } catch (e) {
    setMsg('login-msg', e.message, false);
  }
});

$('logout').addEventListener('click', async () => {
  await api('/api/logout', { method: 'POST' }).catch(() => {});
  showLogin('');
});

$('save-speaker').addEventListener('click', async () => {
  const selected = document.querySelector('input[name=speaker]:checked');
  if (!selected) {
    setMsg('speaker-msg', 'Escolha uma caixa.', false);
    return;
  }
  try {
    await api('/api/speaker', { method: 'POST', body: { address: selected.value } });
    setMsg('speaker-msg', 'Caixa salva. A conexão pode levar até 30 segundos.', true);
    await loadDevices();
  } catch (e) {
    setMsg('speaker-msg', e.message, false);
  }
});

$('test-sound').addEventListener('click', async () => {
  try {
    await api('/api/test-sound', { method: 'POST' });
    setMsg('speaker-msg', 'Som de teste enviado para a caixa.', true);
  } catch (e) {
    setMsg('speaker-msg', e.message, false);
  }
});

$('refresh-logs').addEventListener('click', loadLogs);

$('password-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  if ($('new').value !== $('confirm').value) {
    setMsg('password-msg', 'As senhas novas não são iguais.', false);
    return;
  }
  try {
    await api('/api/password', { method: 'POST', body: { current: $('current').value, new: $('new').value } });
    event.target.reset();
    showLogin('Senha alterada. Entre com a nova senha.');
  } catch (e) {
    setMsg('password-msg', e.message, false);
  }
});

(async () => {
  try {
    const session = await api('/api/session');
    if (session.authenticated) {
      await showApp();
    } else if (!session.passwordConfigured) {
      showLogin('Senha ainda não definida. Um administrador precisa rodar: MarumbiSom.Agent.exe --set-local-password');
    } else {
      showLogin('');
    }
  } catch (e) {
    showLogin(e.message);
  }
})();
