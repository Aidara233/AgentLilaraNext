/* A dialog with fields, because every write in this console needs the same three things: an
   argument shape a person can see, a place to show the server's refusal, and no page reload.

   The rules it encodes:
   - the form owns validation of "required" only; everything else is the server's answer, shown where
     the person is looking instead of in a toast they will miss;
   - a failure keeps the dialog open with the values intact — retyping a provider's configuration
     because one field was wrong is how people learn to hate a settings page;
   - one submit at a time, so a double click cannot create two channels. */

import { h, reason, clear } from './ui.js';

let host = null;

function overlay() {
  host ??= document.getElementById('modal');
  return host;
}

/** Subfield kinds: text, password, number, select, textarea, checkbox. */
function control(field, value, onChange) {
  if (field.type === 'select') {
    const node = h('select', { class: 'field-input' },
      (field.options ?? []).map((option) => h('option', {
        value: option.value,
        text: option.label ?? option.value,
        selected: String(option.value) === String(value ?? ''),
      })));
    node.addEventListener('change', () => onChange(node.value));
    return node;
  }
  if (field.type === 'textarea') {
    const node = h('textarea', { class: 'field-input', rows: field.rows ?? 6, value: value ?? '', placeholder: field.placeholder ?? '' });
    node.addEventListener('input', () => onChange(node.value));
    return node;
  }
  if (field.type === 'checkbox') {
    const node = h('input', { class: 'field-input', type: 'checkbox', checked: Boolean(value) });
    node.addEventListener('change', () => onChange(node.checked));
    return node;
  }
  const node = h('input', {
    class: 'field-input',
    type: field.type === 'number' ? 'number' : field.type === 'password' ? 'password' : 'text',
    value: value ?? '',
    placeholder: field.placeholder ?? '',
  });
  node.addEventListener('input', () => onChange(field.type === 'number' ? node.value : node.value));
  return node;
}

function rowEditor(field, values, redraw) {
  const list = h('div', { class: 'rows' });
  // A field can say "at most one row" — the channel binding is one, because a Channel serves one conversation and a
  // form that lets a person build something the action will refuse is a form that teaches the wrong model.
  const full = field.maxRows !== undefined && values.length >= field.maxRows;
  const add = h('button', { class: 'act', type: 'button', text: field.addLabel ?? '添加一行' });
  add.hidden = full;
  add.addEventListener('click', () => { values.push(field.blank()); redraw(); });

  const removable = field.maxRows === undefined || field.maxRows > 1;
  values.forEach((row, index) => {
    const cells = field.columns.map((column) => {
      const cell = control(column, row[column.name], (next) => { row[column.name] = next; });
      return h('label', { class: 'field' }, h('span', { class: 'field-label', text: column.label ?? column.name }), cell);
    });
    const remove = h('button', { class: 'act danger', type: 'button', text: '删除' });
    remove.hidden = !removable && values.length === 1;
    remove.addEventListener('click', () => { values.splice(index, 1); redraw(); });
    list.append(h('div', { class: 'row' }, cells, removable || values.length > 1 ? remove : null));
  });

  return h('div', {}, list, add);
}

/**
 * @param {object} options
 * @param {string} options.title
 * @param {string} [options.hint]  One line under the title.
 * @param {Array<object>} options.fields  Each: { name, label, type, value, required, hint, options, rows,
 *                                          columns, blank, addLabel }.
 * @param {string} [options.submit]
 * @param {(values: object, api: { close: () => void, fail: (message: string) => void }) => Promise<void>} options.onSubmit
 */
export function openForm({ title, hint, fields, submit = '保存', onSubmit }) {
  const values = {};
  for (const field of fields) {
    values[field.name] = field.type === 'rows'
      ? (field.value ?? []).map((row) => ({ ...row }))
      : field.value;
  }

  const host_ = overlay();
  if (!host_) return null;
  clear(host_);
  host_.hidden = false;

  const failure = h('div', { class: 'note bad', hidden: true });
  const inputs = h('div', { class: 'fields' });
  const submitButton = h('button', { class: 'act', type: 'submit', text: submit });
  const cancelButton = h('button', { class: 'act', type: 'button', text: '取消' });

  const close = () => {
    host_.hidden = true;
    clear(host_);
    document.removeEventListener('keydown', onKey);
  };
  const onKey = (event) => { if (event.key === 'Escape') close(); };
  document.addEventListener('keydown', onKey);

  const fail = (message) => {
    failure.textContent = message;
    failure.hidden = false;
  };

  const redraw = () => {
    clear(inputs);
    for (const field of fields) {
      const label = h('div', { class: 'field-label', text: field.label ?? field.name });
      const control_ = field.type === 'rows'
        ? rowEditor(field, values[field.name], redraw)
        : control(field, values[field.name], (next) => { values[field.name] = next; });
      inputs.append(h('label', { class: 'field' }, label, control_,
        field.hint ? h('span', { class: 'field-hint', text: field.hint }) : null));
    }
  };
  redraw();

  const form = h('form', { class: 'dialog' },
    h('h2', { text: title }),
    hint ? h('p', { class: 'hint', text: hint }) : null,
    failure,
    inputs,
    h('div', { class: 'dialog-actions' }, cancelButton, submitButton));

  cancelButton.addEventListener('click', close);
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    failure.hidden = true;
    const missing = fields.find((field) => field.required &&
      (field.type === 'rows' ? !(values[field.name] ?? []).length : String(values[field.name] ?? '').trim() === ''));
    if (missing) return fail(`「${missing.label ?? missing.name}」是必填的。`);

    submitButton.disabled = true;
    try {
      await onSubmit(values, { close, fail });
    } catch (error) {
      fail(reason(error));
    } finally {
      submitButton.disabled = false;
    }
  });

  host_.append(form);
  inputs.querySelector('input, select, textarea')?.focus();
  return { close };
}

/** Read-only dialog for a fetched document (a provider's configuration, a script's source). */
export function openDocument({ title, hint, lines, text, actions = [] }) {
  const host_ = overlay();
  if (!host_) return null;
  clear(host_);
  host_.hidden = false;

  const close = () => { host_.hidden = true; clear(host_); };
  const body = h('div', { class: 'document' });
  for (const line of lines ?? []) body.append(line);
  if (text !== undefined) body.append(h('pre', { class: 'source', text }));
  const buttons = h('div', { class: 'dialog-actions' },
    ...actions.map((action) => h('button', {
      class: 'act', type: 'button', text: action.label,
      onclick: () => { close(); action.run(); },
    })),
    h('button', { class: 'act', type: 'button', text: '关闭', onclick: close }));

  host_.append(h('div', { class: 'dialog wide' }, h('h2', { text: title }), hint ? h('p', { class: 'hint', text: hint }) : null, body, buttons));
  return { close };
}
