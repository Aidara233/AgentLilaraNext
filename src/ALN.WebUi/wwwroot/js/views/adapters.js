/* Adapters: the mailbox side. Start and stop only — creating an adapter is a written spec on disk,
   and there is no action for that yet, so this page does not pretend to offer it. */

import { panel, dataTable, stateBadge, badge } from '../ui.js';
import { actionButton } from './shared.js';

const columns = (ctx) => [
  { title: '地址', class: 'mono', cell: (row) => row.address },
  { title: '平台', cell: (row) => row.platform },
  { title: '账号', class: 'mono', cell: (row) => row.account },
  { title: '方向', cell: (row) => badge(row.direction ?? '—') },
  {
    title: '状态',
    cell: (row) => stateBadge(row.state),
  },
  {
    title: '',
    class: 'actions',
    cell: (row) => {
      const running = /running/i.test(row.state ?? '');
      return running
        ? actionButton(ctx, {
          action: 'adapters.stop',
          args: { address: row.address },
          label: '停止',
          danger: true,
          ask: `停止 ${row.address}？它当前的收件箱将不再被轮询。`,
          done: `${row.address} 已停止`,
        })
        : actionButton(ctx, {
          action: 'adapters.start',
          args: { address: row.address },
          label: '启动',
          ask: `启动 ${row.address}？`,
          done: `${row.address} 已启动`,
        });
    },
  },
];

export const adapters = {
  id: 'adapters',
  title: '适配器',
  hint: '启动与停止只影响本进程内的连接状态；规格文件不会被改写。',

  async mount(root, ctx) {
    if (!ctx.allowed('adapters.list')) {
      root.append(ctx.noPermission('adapters.list'));
      return;
    }
    const rows = (await ctx.api.invoke('adapters.list')) ?? [];
    root.append(panel(`适配器（${rows.length}）`,
      dataTable({ columns: columns(ctx), rows, empty: '还没有适配器。' })));
  },
};
