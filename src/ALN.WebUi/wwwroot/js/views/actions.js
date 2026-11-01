/* The action catalog, plus a raw caller.

   The catalog is the real answer to "what can this console do": it comes from the server, carries
   the capability each action needs, and is what makes the disabled buttons elsewhere explainable.
   The raw caller exists because a dedicated form for every action is a losing race — when a new
   action ships, this page can call it before anyone builds a screen for it. */

import { h, kv, panel, dataTable, badge, note, reason } from '../ui.js';

const columns = [
  { title: 'id', class: 'mono', cell: (row) => row.id },
  { title: '摘要', cell: (row) => row.summary },
  { title: '所需能力', class: 'mono', cell: (row) => row.capability },
  { title: '变更状态', cell: (row) => badge(row.mutating ? '是' : '否', row.mutating ? 'warn' : '') },
  { title: '当前身份', cell: (row) => badge(row.allowed ? '允许' : '不足', row.allowed ? 'ok' : 'bad') },
];

export const actions = {
  id: 'actions',
  title: '动作目录',
  nav: '动作',
  hint: '前端能做的全部事情都在这里；能力不足的按钮在别处也是灰的，原因同一句。',

  async mount(root, ctx) {
    const allowedCount = ctx.actions.filter((action) => action.allowed).length;
    root.append(panel(`动作（${allowedCount} / ${ctx.actions.length} 允许）`,
      dataTable({ columns, rows: ctx.actions, empty: '核心没有注册任何动作。' })));

    // ---- raw caller ----

    const permitted = ctx.actions.filter((action) => action.allowed);
    const select = h('select', {}, permitted.map((action) => h('option', {
      value: action.id,
      text: `${action.id}${action.mutating ? ' · 变更' : ''}`,
    })));
    const args = h('textarea', { spellcheck: 'false', rows: 6, value: '{}' });
    const output = h('pre', { class: 'out', text: '（结果会出现在这里）' });
    const hint = h('span', { class: 'hint' });

    const sync = () => {
      const selected = ctx.actions.find((action) => action.id === select.value);
      hint.textContent = selected ? `${selected.summary}${selected.mutating ? ' · 会改变状态' : ''}` : '';
    };
    select.addEventListener('change', sync);
    sync();

    const run = h('button', {
      class: 'act',
      type: 'button',
      text: '调用',
      disabled: permitted.length === 0,
      onclick: async (event) => {
        const button = event.currentTarget;
        const action = ctx.actions.find((candidate) => candidate.id === select.value);
        if (!action) return;
        let parsed;
        try {
          parsed = JSON.parse(args.value || '{}');
        } catch (error) {
          output.textContent = `参数不是合法 JSON：${error.message}`;
          return;
        }
        if (action.mutating && !window.confirm(`执行 ${action.id}？这个动作会改变核心状态。`)) return;
        button.disabled = true;
        output.textContent = '调用中…';
        try {
          const data = await ctx.api.invoke(action.id, parsed);
          output.textContent = data === undefined ? '（无返回值）' : JSON.stringify(data, null, 2);
        } catch (error) {
          output.textContent = `失败：${reason(error)}`;
        } finally {
          button.disabled = false;
        }
      },
    });

    root.append(panel('原样调用',
      h('div', { class: 'row' }, select, hint),
      args,
      h('div', { class: 'row' }, run, h('span', { class: 'hint', text: '参数按 JSON 原样发送；空或 {} 表示无参数。' })),
      output,
      permitted.length === 0 ? note('当前身份没有任何被允许的动作，所以这里没有可调用的条目。') : null,
      kv([['接口', ctx.api.base], ['身份', `${ctx.actor?.id ?? '?'} · ${ctx.actor?.kind ?? '?'}`]])));
  },
};
