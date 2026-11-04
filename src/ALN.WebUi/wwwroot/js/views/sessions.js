/* Sessions and the bin.

   A Session is the one thing in the data root nobody wrote a spec for — a Channel's script mints it —
   so it is also the one thing that accumulates. The page therefore has two halves: what exists, and
   what has been put away.

   Archiving is not deleting, and the buttons say so: a Session that was archived can be restored, and
   the reason column comes from whoever archived it. */

import { h, panel, dataTable, badge, clock, note } from '../ui.js';
import { perform } from './shared.js';

const sessionColumns = (ctx) => [
  { title: '会话', class: 'mono', cell: (row) => row.id },
  { title: '频道', class: 'mono', cell: (row) => row.channel ?? '—' },
  { title: '最后活跃', cell: (row) => clock(row.lastActiveAt) },
  { title: '步数', class: 'num', cell: (row) => row.steps ?? 0 },
  { title: '占用', class: 'num', cell: (row) => `${Math.round((row.bytes ?? 0) / 1024)} KB` },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act danger',
      type: 'button',
      text: '归档',
      disabled: !ctx.allowed('archive.move'),
      title: ctx.allowed('archive.move') ? '' : '缺少能力 archive.control',
      onclick: async () => {
        if (!window.confirm(`把会话 ${row.id} 放进回收站？它会从正常视角消失，但可以恢复。`)) return;
        await perform(ctx, {
          action: 'archive.move',
          args: { kind: 'session', ids: [row.id], dryRun: false },
          done: `会话 ${row.id} 已归档`,
        });
      },
    }),
  },
];

const archivedColumns = (ctx) => [
  { title: '条目', class: 'mono', cell: (row) => row.id },
  { title: '类别', cell: (row) => badge(row.kind ?? 'session') },
  { title: '归档于', cell: (row) => clock(row.archivedAt) },
  { title: '占用', class: 'num', cell: (row) => `${Math.round((row.bytes ?? 0) / 1024)} KB` },
  { title: '原因', class: 'muted', cell: (row) => row.reason ?? row.note ?? '—' },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act',
      type: 'button',
      text: '恢复',
      disabled: !ctx.allowed('archive.restore'),
      title: ctx.allowed('archive.restore') ? '' : '缺少能力 archive.control',
      onclick: async () => {
        if (!window.confirm(`把 ${row.id} 恢复回正常视角？`)) return;
        await perform(ctx, {
          action: 'archive.restore',
          args: { kind: row.kind ?? 'session', ids: [row.id], dryRun: false },
          done: `${row.id} 已恢复`,
        });
      },
    }),
  },
];

export const sessions = {
  id: 'sessions',
  title: '会话',
  hint: '会话由频道脚本创建；归档是"放进回收站"，不是删除。',

  async mount(root, ctx) {
    const canList = ctx.allowed("sessions.list");
    const rows = canList ? (await ctx.api.invoke("sessions.list"))?.sessions ?? [] : [];
    root.append(canList
      ? panel(`会话（${rows.length}）`,
        dataTable({ columns: sessionColumns(ctx), rows, empty: "还没有会话。" }))
      : ctx.noPermission('sessions.list'));

    if (!ctx.allowed('archive.list')) return;
    const bin = (await ctx.api.invoke('archive.list', { kind: 'session' })) ?? {};
    const archived = bin.archived ?? [];
    root.append(panel(`回收站（${archived.length}）`,
      dataTable({ columns: archivedColumns(ctx), rows: archived, empty: '回收站是空的。' }),
      note(`共 ${bin.bin?.items ?? 0} 项、${Math.round((bin.bin?.bytes ?? 0) / 1024)} KB。归档是搬走，不是删除；恢复不会重启任何东西。`, 'muted')));
  },
};
