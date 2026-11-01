/* DOM helpers. Small on purpose: no virtual DOM, no templating language, no innerHTML with data in
   it. Every string that came from the core reaches the page through textContent, so a signal detail
   containing markup renders as that markup rather than as a hole. */

function append(node, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false || child === '') continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

/**
 * h('td', { class: 'num' }, '42')
 * Props: class, text, dataset, on<event> handlers, and anything assignable on the element
 * (value, checked, disabled, title, type, …); the rest become attributes.
 */
export function h(tag, props, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (key.startsWith('on') && typeof value === 'function') {
      node.addEventListener(key.slice(2).toLowerCase(), value);
    } else if (key in node) node[key] = value;
    else node.setAttribute(key, String(value));
  }
  return append(node, children);
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

export function badge(text, kind = '') {
  return h('span', { class: `badge ${kind}`.trim(), text });
}

/** State strings come from C# enums; the mapping is presentation only. */
export function stateBadge(state) {
  const value = String(state ?? '');
  const kind = /running|ok|started/i.test(value) ? 'ok'
    : /stopped|disabled/i.test(value) ? ''
    : /failed|error|unavailable/i.test(value) ? 'bad'
    : 'warn';
  return badge(value || '未知', kind);
}

export function levelBadge(level) {
  const value = String(level ?? '');
  const kind = /error|fatal|critical/i.test(value) ? 'bad'
    : /warn/i.test(value) ? 'warn'
    : /debug|trace|verbose/i.test(value) ? ''
    : 'info';
  return badge(value || '—', kind);
}

const pad = (n) => String(n).padStart(2, '0');

/** Server timestamps are ISO; render in the viewer's zone, and only add the date when it isn't today. */
export function clock(value) {
  if (!value) return '—';
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return String(value);
  const now = new Date();
  const sameDay = at.toDateString() === now.toDateString();
  const time = `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`;
  return sameDay ? time : `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${time}`;
}

export function duration(ms) {
  if (ms === null || ms === undefined) return '—';
  const value = Number(ms);
  if (!Number.isFinite(value)) return '—';
  return value >= 1000 ? `${(value / 1000).toFixed(2)} s` : `${value.toFixed(1)} ms`;
}

/** Failure text without a stack: the message plus the code, which is what tells Denied from Invalid. */
export function reason(error) {
  if (!error) return '';
  const code = error.code && error.code !== undefined ? ` [${error.code}]` : '';
  return `${error.message ?? error}${code}`;
}

/** Wraps a table so every list view has the same chrome. */
export function panel(title, ...children) {
  const body = h('div', { class: 'body' }, children);
  return h('section', { class: 'panel' }, title ? h('h2', { text: title }) : null, body);
}

/**
 * columns: [{ title, class?, cell?(row) }] — cell should return text or a node.
 */
export function dataTable({ columns, rows, empty = '没有数据。' }) {
  const head = h('thead', {}, h('tr', {}, columns.map((column) => h('th', { class: column.class, text: column.title }))));
  const body = h('tbody');
  if (!rows.length) {
    body.append(h('tr', {}, h('td', { class: 'empty', colspan: String(columns.length), text: empty })));
  } else {
    for (const row of rows) {
      body.append(h('tr', {}, columns.map((column) => h('td', { class: column.class }, column.cell ? column.cell(row) : row[column.key]))));
    }
  }
  return h('table', {}, head, body);
}

/** Definition list for small key/value blocks; values are strings or nodes. */
export function kv(pairs) {
  const list = h('dl', { class: 'kv' });
  for (const [key, value] of pairs) {
    if (value === null || value === undefined || value === '') continue;
    list.append(h('dt', { text: key }), h('dd', {}, value));
  }
  return list;
}

export function stat(label, value, note) {
  return h('div', { class: 'stat' },
    h('div', { class: 'k', text: label }),
    h('div', { class: 'v', text: String(value) }),
    note ? h('div', { class: 'n', text: note }) : null);
}

export function note(text, kind = '') {
  return h('div', { class: `note ${kind}`.trim(), text });
}

/** <details> with the value pretty-printed; used for signal details, which can be any JSON shape. */
export function detailBlock(value) {
  if (value === null || value === undefined || value === '') return h('span', { class: 'muted', text: '—' });
  const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
  const flat = text.replace(/\s+/g, ' ');
  const preview = flat.length > 90 ? `${flat.slice(0, 90)}…` : flat;
  return h('details', {},
    h('summary', { text: preview }),
    h('pre', { text }));
}

let toastHost = null;

export function toast(message, kind = '') {
  toastHost ??= document.getElementById('toasts');
  if (!toastHost) return;
  const node = h('div', { class: `toast ${kind}`.trim(), text: message });
  toastHost.append(node);
  setTimeout(() => node.remove(), kind === 'bad' ? 8000 : 4000);
}
