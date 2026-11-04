/* Scripts: what is loaded, whether it is switched on, where it is mounted, and its source.

   Two different switches, kept visibly separate because they are not the same thing:
   - enabled: whether the script is in the catalog at all (`scripts.set`);
   - mounted: which instance it runs on (`graph.mount` / `graph.unmount`). A "manual" script runs
     nowhere until somebody mounts it. */

import { h, panel, dataTable, badge } from '../ui.js';
import { perform } from './shared.js';
import { openDocument, openForm } from '../form.js';

const scriptColumns = (ctx) => [
  { title: 'id', class: 'mono', cell: (row) => row.id },
  { title: '库', cell: (row) => badge(row.library ?? 'user') },
  { title: '挂载方式', cell: (row) => badge(row.mount) },
  { title: '信任域', cell: (row) => badge(row.trust) },
  { title: '启用', cell: (row) => (row.enabled ? badge('已启用', 'ok') : badge('已停用', '')) },
  { title: '挂载数', class: 'num', cell: (row) => row.mounted ?? 0 },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('span', { class: 'actions' },
      h('button', {
        class: 'act',
        type: 'button',
        text: '源码',
        disabled: !ctx.allowed('scripts.get'),
        title: ctx.allowed('scripts.get') ? '' : '缺少能力 graph.read',
        onclick: () => openSource(ctx, row.id),
      }),
      h('button', {
        class: 'act',
        type: 'button',
        text: '挂载…',
        disabled: !ctx.allowed('graph.mount'),
        title: ctx.allowed('graph.mount') ? '' : '缺少能力 graph.control',
        onclick: () => openMount(ctx, row.id),
      }),
      h('button', {
        class: `act${row.enabled ? ' danger' : ''}`,
        type: 'button',
        text: row.enabled ? '停用' : '启用',
        disabled: !ctx.allowed('scripts.set'),
        title: ctx.allowed('scripts.set') ? '' : '缺少能力 graph.control',
        onclick: async () => {
          if (row.enabled && !window.confirm(`停用 ${row.id}？挂在这个脚本上的规则会停止工作。`)) return;
          await perform(ctx, {
            action: 'scripts.set',
            args: { target: row.id, enabled: !row.enabled },
            done: `${row.id} 已${row.enabled ? '停用' : '启用'}`,
          });
        },
      })),
  },
];

const ruleColumns = (ctx) => [
  { title: '规则', class: 'mono', cell: (row) => row.id },
  { title: '脚本', class: 'mono', cell: (row) => row.script },
  { title: '范围', cell: (row) => `${row.scope}${row.selector ? ` · ${row.selector}` : ''}` },
  { title: '来源', cell: (row) => row.source ?? '—' },
  { title: '剩余', class: 'num', cell: (row) => (row.remaining ?? '不限') },
  { title: '状态', cell: (row) => (row.pending ? badge('等待目标', 'warn') : badge('生效', 'ok')) },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act danger',
      type: 'button',
      text: '卸载',
      disabled: !ctx.allowed('graph.unmount'),
      title: ctx.allowed('graph.unmount') ? '' : '缺少能力 graph.control',
      onclick: async () => {
        if (!window.confirm(`删除挂载规则 ${row.id}？`)) return;
        await perform(ctx, { action: 'graph.unmount', args: { rule: row.id }, done: `规则 ${row.id} 已删除` });
      },
    }),
  },
];

const mountedColumns = [
  { title: '脚本', class: 'mono', cell: (row) => row.script },
  { title: '目标', class: 'mono', cell: (row) => row.target },
  { title: '规则', class: 'mono', cell: (row) => row.rule ?? '—' },
  { title: '剩余派发', class: 'num', cell: (row) => (row.remaining ?? '不限') },
];

async function openSource(ctx, id) {
  try {
    const detail = await ctx.api.invoke('scripts.get', { id });
    openDocument({
      title: `${detail.id} v${detail.version}`,
      hint: `${detail.library} · ${detail.targetType} · ${detail.mount} · ${detail.trust} · 指纹 ${detail.fingerprint ?? '—'}`,
      text: detail.source ?? '',
    });
  } catch (error) {
    ctx.notify(`读取源码失败：${error.message}`, 'bad');
  }
}

function openMount(ctx, id) {
  openForm({
    title: `把 ${id} 挂到一个实例上`,
    hint: '目标写成实例地址，例如 channel.conversation#chat-auto 或 adapter.file#file-1。',
    fields: [
      { name: 'target', label: '目标实例', type: 'text', placeholder: 'channel.conversation#chat-auto', required: true },
    ],
    submit: '挂载',
    onSubmit: async (values, dialog) => {
      await perform(ctx, {
        action: 'graph.mount',
        args: { script: id, target: values.target },
        done: `${id} → ${values.target} 已挂载`,
      });
      dialog.close();
    },
  });
}

export async function mountScripts(root, ctx) {
  const listed = (await ctx.api.invoke('scripts.list')) ?? {};
  const scripts = listed.scripts ?? [];
  const graphed = ctx.allowed('graph.list') ? (await ctx.api.invoke('graph.list')) ?? {} : {};

  root.append(
    panel(`脚本（${scripts.length}）`,
      dataTable({ columns: scriptColumns(ctx), rows: scripts, empty: '还没有加载任何脚本包。' })),
    panel(`当前挂载（${(graphed.mounted ?? []).length}）`,
      dataTable({ columns: mountedColumns, rows: graphed.mounted ?? [], empty: '没有挂载。' })),
    panel(`挂载规则（${(graphed.rules ?? []).length}）`,
      dataTable({ columns: ruleColumns(ctx), rows: graphed.rules ?? [], empty: '没有持久化的挂载规则。' })));
}

export const scripts = {
  id: 'scripts',
  title: '脚本',
  hint: '"启用"决定脚本在不在目录里；"挂载"决定它跑在哪个实例上。这两个是两件事。',
  mount: mountScripts,
};
