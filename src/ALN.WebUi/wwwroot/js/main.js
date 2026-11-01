/* Shell: identity, routing, refresh. Three rules the views can rely on:
   - the token lives in sessionStorage and is re-sent on every request; there is no session cookie;
   - a view is mounted into a fresh container, so nothing has to clean up after itself except a timer;
   - 401 anywhere means the gate opens again, because a stale token makes every screen a lie. */

import { Api, ApiError, normalizeBase, explain } from './api.js';
import { h, clear, toast, reason } from './ui.js';
import { overview } from './views/overview.js';
import { adapters } from './views/adapters.js';
import { channels } from './views/channels.js';
import { models } from './views/models.js';
import { signals } from './views/signals.js';
import { actions } from './views/actions.js';

const VIEWS = [overview, adapters, channels, models, signals, actions];
const KEY_BASE = 'aln.api';
const KEY_TOKEN = 'aln.token';
const KEY_LIVE = 'aln.live';
const REFRESH_MS = 5000;

const state = {
  api: null,
  actor: null,
  actions: [],
  view: null,
  dispose: null,
  timer: null,
  busy: false,
};

const dom = {
  login: document.getElementById('login'),
  app: document.getElementById('app'),
  loginForm: document.getElementById('login-form'),
  apiInput: document.getElementById('f-api'),
  tokenInput: document.getElementById('f-token'),
  submit: document.getElementById('f-submit'),
  loginError: document.getElementById('login-error'),
  nav: document.getElementById('nav'),
  view: document.getElementById('view'),
  actor: document.getElementById('actor'),
  conn: document.getElementById('conn'),
  signout: document.getElementById('signout'),
  status: document.getElementById('status'),
  endpoint: document.getElementById('endpoint'),
  live: document.getElementById('live'),
};

const allowed = (actionId) => state.actions.some((action) => action.id === actionId && action.allowed);
const capabilityOf = (actionId) => state.actions.find((action) => action.id === actionId)?.capability ?? '未知能力';

/** What a view receives. Everything a view may touch is here; it never imports the client itself. */
function context(refresh) {
  return {
    api: state.api,
    actor: state.actor,
    actions: state.actions,
    allowed,
    refresh,
    notify: toast,
    /** Standard "this identity cannot do that" block, so every view says it the same way. */
    noPermission(actionId) {
      return h('div', { class: 'note' },
        `当前身份没有读取 ${actionId} 所需的能力（${capabilityOf(actionId)}）。`,
        h('div', { class: 'hint', text: '在 api/server.json 里给这个令牌的角色加上对应能力，然后重新连接。' }));
    },
  };
}

function setConnection(kind, text) {
  dom.conn.className = `conn ${kind}`.trim();
  dom.conn.title = text;
  dom.status.textContent = text;
}

function showLogin(message = '') {
  stopLive();
  dom.app.hidden = true;
  dom.login.hidden = false;
  dom.loginError.textContent = message;
  dom.tokenInput.value = '';
  dom.tokenInput.focus();
  state.api = null;
  state.actor = null;
  state.actions = [];
}

function showApp() {
  dom.login.hidden = true;
  dom.app.hidden = false;
  dom.actor.textContent = `${state.actor?.id ?? '?'} · ${state.actor?.kind ?? '?'}`;
  dom.endpoint.textContent = state.api.base;
  buildNav();
}

function buildNav() {
  clear(dom.nav);
  for (const view of VIEWS) {
    dom.nav.append(h('a', {
      href: `#/${view.id}`,
      text: view.nav ?? view.title,
      class: state.view === view.id ? 'on' : '',
      'data-id': view.id,
    }));
  }
}

async function connect(base, token, { quiet = false } = {}) {
  setConnection('', '连接中…');
  let api;
  try {
    api = new Api(base, token);
    const catalog = await api.catalog();
    state.api = api;
    state.actor = catalog.actor;
    state.actions = catalog.actions ?? [];
  } catch (error) {
    const message = error instanceof ApiError && error.status === 0
      ? explain(error)
      : `连接失败：${explain(error)}`;
    if (!quiet) showLogin(message);
    else showLogin('');
    setConnection('bad', '未连接');
    return false;
  }

  sessionStorage.setItem(KEY_BASE, api.base);
  sessionStorage.setItem(KEY_TOKEN, token);
  setConnection('ok', `已连接 ${api.base}`);
  showApp();
  route();
  return true;
}

/** One render path for both "navigate" and "refresh", so a stale panel can't survive a reload. */
async function route() {
  const wanted = (location.hash.replace(/^#\/?/, '') || 'overview').split('?')[0];
  const view = VIEWS.find((candidate) => candidate.id === wanted) ?? VIEWS[0];
  if (!state.api) return;

  state.dispose?.();
  state.dispose = null;
  state.view = view.id;
  buildNav();

  clear(dom.view);
  const head = h('div', { class: 'view-head' },
    h('h1', { text: view.title }),
    view.hint ? h('p', { class: 'hint', text: view.hint }) : null);
  const body = h('div', { class: 'view-body' });
  dom.view.append(head, body);

  const refresh = () => route();
  state.busy = true;
  try {
    state.dispose = (await view.mount(body, context(refresh))) ?? null;
    setConnection('ok', `已连接 ${state.api.base} · ${new Date().toLocaleTimeString()}`);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      showLogin('令牌被拒绝或已失效，请重新输入。');
      return;
    }
    body.append(h('div', { class: 'note' }, `这个页面没能加载：${reason(error)}`));
    setConnection('bad', `请求失败 · ${reason(error)}`);
  } finally {
    state.busy = false;
  }
}

function startLive() {
  stopLive();
  state.timer = setInterval(() => {
    if (document.hidden || state.busy) return;
    route();
  }, REFRESH_MS);
}

function stopLive() {
  if (state.timer) clearInterval(state.timer);
  state.timer = null;
}

function signOut() {
  sessionStorage.removeItem(KEY_TOKEN);
  stopLive();
  dom.live.checked = false;
  sessionStorage.setItem(KEY_LIVE, '0');
  showLogin('');
}

// ---- wiring ----

dom.loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  dom.loginError.textContent = '';
  dom.submit.disabled = true;
  try {
    const base = normalizeBase(dom.apiInput.value);
    await connect(base, dom.tokenInput.value.trim());
  } catch (error) {
    dom.loginError.textContent = explain(error);
  } finally {
    dom.submit.disabled = false;
  }
});

dom.signout.addEventListener('click', signOut);

window.addEventListener('hashchange', () => { if (state.api) route(); });

dom.live.addEventListener('change', () => {
  sessionStorage.setItem(KEY_LIVE, dom.live.checked ? '1' : '0');
  if (dom.live.checked) startLive(); else stopLive();
});

document.addEventListener('visibilitychange', () => {
  if (!document.hidden && dom.live.checked && state.api) route();
});

// ---- boot ----

(async function boot() {
  let defaultApi = '';
  try {
    const response = await fetch('./config.json', { headers: { Accept: 'application/json' } });
    if (response.ok) defaultApi = (await response.json())?.api ?? '';
  } catch {
    // Served by something that isn't our host: the login form still lets a human type the address.
  }

  dom.apiInput.value = sessionStorage.getItem(KEY_BASE) || defaultApi || 'http://127.0.0.1:5199';
  dom.live.checked = sessionStorage.getItem(KEY_LIVE) === '1';

  const token = sessionStorage.getItem(KEY_TOKEN);
  if (token) {
    const resumed = await connect(dom.apiInput.value, token, { quiet: true });
    if (resumed && dom.live.checked) startLive();
  } else {
    showLogin('');
  }
})();
