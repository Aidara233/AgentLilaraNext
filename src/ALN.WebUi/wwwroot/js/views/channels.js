/* Channels: address, bindings, running state. A binding names an adapter and a platform
   conversation; whether that adapter is up is decided at delivery time, not here, so this page shows
   the binding as recorded and leaves "will it actually arrive" to the adapters page. */

import { h, panel, dataTable, stateBadge } from '../ui.js';
import { actionButton } from './shared.js';

const bindingList = (row) => {
  const bindings = row.bindings ?? [];
  if (!bindings.length) return h('span', { class: 'muted', text: '无绑定' });
  return h('div', { class: 'bindings' },
    bindings.map((binding) => h('div', { text: `${binding.adapter} → ${binding.conversation}` })));
};

const columns = (ctx) => [
  { title: '地址', class: 'mono', cell: (row) => row.address },
  { title: '名称', cell: (row) => row.name ?? '—' },
  { title: '状态', cell: (row) => stateBadge(row.running ? 'Running' : 'Stopped') },
  { title: '绑定', cell: bindingList },
  {
    title: '',
    class: 'actions',
    cell: (row) => (row.running
      ? actionButton(ctx, {
        action: 'channels.stop',
        args: { address: row.address },
        label: '停止',
        danger: true,
        ask: `停止频道 ${row.address}？投递到它的消息会失败，直到重新启动。`,
        done: `${row.address} 已停止`,
      })
      : actionButton(ctx, {
        action: 'channels.start',
        args: { address: row.address },
        label: '启动',
        ask: `启动频道 ${row.address}？`,
        done: `${row.address} 已启动`,
      })),
  },
];

export const channels = {
  id: 'channels',
  title: '频道',
  hint: '频道把会话与适配器上的具体会话绑定起来；绑定只是形状校验，可用性在投递时判断。',

  async mount(root, ctx) {
    if (!ctx.allowed('channels.list')) {
      root.append(ctx.noPermission('channels.list'));
      return;
    }
    const rows = (await ctx.api.invoke('channels.list')) ?? [];
    root.append(panel(`频道（${rows.length}）`,
      dataTable({ columns: columns(ctx), rows, empty: '还没有频道。频道由脚本侧的 channel.route 创建。' })));
  },
};
