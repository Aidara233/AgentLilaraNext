/* Parameters: every tunable the running components expose, grouped by the component that owns it.

   Each row is one value with its own save button rather than a page-wide "save", because these apply
   the moment they are set (or say that they need a restart). A form that batches them would make
   "which of my five edits took effect" a question nobody can answer.

   What is shown comes from the owner: kind, range, and *when* it applies. A UI that hides "restart
   required" turns a careful change into a mystery. */

import { h, panel, dataTable, badge, note } from '../ui.js';
import { perform } from './shared.js';

const appliesLabel = {
  immediate: '立即',
  nextround: '下一轮',
  reconnect: '重连后',
  restart: '重启后',
};

const columns = (ctx) => [
  { title: '参数', class: 'mono', cell: (row) => row.name },
  { title: '类型', cell: (row) => row.kind },
  { title: '默认', class: 'mono', cell: (row) => row.defaultValue ?? '—' },
  {
    title: '当前值',
    cell: (row) => {
      if (row.secret) return h('span', { class: 'muted', text: '（只写不读）' });
      const input = h('input', {
        class: 'field-input',
        type: row.kind === 'integer' || row.kind === 'number' ? 'number' : 'text',
        value: row.value ?? '',
        placeholder: row.defaultValue ?? '',
        min: row.minimum ?? undefined,
        max: row.maximum ?? undefined,
      });
      input.dataset.name = row.name;
      input.dataset.scope = row.scope;
      return input;
    },
  },
  {
    title: '生效',
    cell: (row) => badge(appliesLabel[String(row.apply).toLowerCase()] ?? row.apply),
  },
  { title: '说明', class: 'muted', cell: (row) => row.description ?? '' },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act',
      type: 'button',
      text: '保存',
      disabled: !ctx.allowed('params.set'),
      title: ctx.allowed('params.set') ? '' : '缺少能力 params.write',
      dataset: { scope: row.scope, name: row.name },
      onclick: async (event) => {
        const button = event.currentTarget;
        const input = button.closest('tr')?.querySelector('input');
        await perform(ctx, {
          action: 'params.set',
          args: { scope: row.scope, name: row.name, value: input?.value ?? '' },
          done: `${row.name} 已更新`,
          button,
        });
      },
    }),
  },
];

export const params = {
  id: 'params',
  title: '参数',
  hint: '组件自己校验、立即应用、再持久化。写失败不会留下"文件说一套、进程做一套"。',

  async mount(root, ctx) {
    if (!ctx.allowed('params.list')) {
      root.append(ctx.noPermission('params.list'));
      return;
    }
    const listed = (await ctx.api.invoke('params.list')) ?? {};
    const parameters = listed.parameters ?? [];
    if (!parameters.length) {
      root.append(panel('参数', note('当前没有任何组件登记了可调项。')));
      return;
    }

    const byScope = new Map();
    for (const parameter of parameters) {
      if (!byScope.has(parameter.scope)) byScope.set(parameter.scope, []);
      byScope.get(parameter.scope).push(parameter);
    }
    for (const [scope, rows] of byScope) {
      root.append(panel(`${scope}（${rows.length}）`,
        dataTable({ columns: columns(ctx), rows, empty: '没有参数。' })));
    }
    root.append(note('生效方式来自组件自己：标"重启后"的那些改了不会立刻变，别拿它当"没生效"来排查。', 'muted'));
  },
};
