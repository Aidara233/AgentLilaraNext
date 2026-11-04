/* Channels: address, bindings, running state, and the two things a person configuring one needs —
   making it, and sending one message through it to see whether the wiring holds.

   A binding names an adapter and a platform conversation; whether that adapter is up is decided at
   delivery time, not here, so this page shows the binding as recorded and leaves "will it actually
   arrive" to the adapters page. */

import { h, panel, dataTable, stateBadge } from '../ui.js';
import { actionButton, perform } from './shared.js';
import { openForm } from '../form.js';

const bindingList = (row) => {
  const bindings = row.bindings ?? [];
  if (!bindings.length) return h('span', { class: 'muted', text: '无绑定' });
  return h('div', { class: 'bindings' },
    bindings.map((binding) => h('div', { text: `${binding.adapter} → ${binding.conversation}` })));
};

const columns = (ctx) => [
  { title: '地址', class: 'mono', cell: (row) => row.address },
  { title: '名称', cell: (row) => row.name ?? '—' },
  { title: '类型', class: 'mono', cell: (row) => row.typeId ?? '—' },
  { title: '窗口', class: 'num', cell: (row) => (row.maxMessages ?? '不限') },
  { title: '状态', cell: (row) => stateBadge(row.running ? 'Running' : 'Stopped') },
  { title: '绑定', cell: bindingList },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('span', { class: 'actions' },
      row.running
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
        }),
      h('button', {
        class: 'act',
        type: 'button',
        text: '发一条',
        disabled: !ctx.allowed('channels.send'),
        title: ctx.allowed('channels.send') ? '' : '缺少能力 messages.write',
        onclick: () => openSend(ctx, row),
      }),
      actionButton(ctx, {
        action: 'channels.destroy',
        args: { address: row.address },
        label: '删除',
        danger: true,
        ask: `删除频道 ${row.address}？它的规格文件会一并删掉；会话目录不会被动。`,
        done: `${row.address} 已删除`,
      })),
  },
];

async function channelTypes(api) {
  try {
    const types = (await api.invoke('system.types'))?.types ?? [];
    return types.filter((type) => !type.abstract && String(type.typeId).startsWith('channel.'));
  } catch {
    return [];
  }
}

async function adapterAddresses(api) {
  try {
    return ((await api.invoke('adapters.list')) ?? []).map((row) => row.address);
  } catch {
    return [];
  }
}

function openSend(ctx, row) {
  openForm({
    title: `通过 ${row.name ?? row.address} 发一条`,
    hint: '走这个频道的第一个绑定发出去；能不能到达取决于那个适配器是否可用。',
    fields: [{ name: 'text', label: '内容', type: 'textarea', rows: 4, required: true }],
    submit: '发送',
    onSubmit: async (values, dialog) => {
      await perform(ctx, {
        action: 'channels.send',
        args: { address: row.address, text: values.text },
        done: '已交给适配器发送',
      });
      dialog.close();
    },
  });
}

async function openCreate(ctx) {
  const [types, adapters] = await Promise.all([channelTypes(ctx.api), adapterAddresses(ctx.api)]);
  const typeOptions = types.map((type) => ({ value: type.typeId, label: type.typeId }));
  const adapterOptions = adapters.map((address) => ({ value: address, label: address }));

  openForm({
    title: '新建频道',
    hint: '地址由类型和实例名拼出来：channel.conversation + group-1 → channel.conversation#group-1。',
    fields: [
      typeOptions.length
        ? { name: 'type', label: '类型', type: 'select', options: typeOptions, value: typeOptions[0].value, required: true }
        : { name: 'type', label: '类型', type: 'text', placeholder: 'channel.conversation', required: true },
      { name: 'instance', label: '实例名', type: 'text', placeholder: 'group-1', required: true },
      { name: 'name', label: '显示名', type: 'text', placeholder: '留空 = 实例名' },
      { name: 'maxMessages', label: '一轮最多带多少条', type: 'number', hint: '留空 = 不限；必须 > 0，0 不会被当成"不限"。' },
      {
        name: 'bindings',
        label: '绑定',
        type: 'rows',
        addLabel: '添加一个绑定',
        blank: () => ({ adapter: adapterOptions[0]?.value ?? '', conversation: '' }),
        columns: [
          adapterOptions.length
            ? { name: 'adapter', label: '适配器', type: 'select', options: adapterOptions }
            : { name: 'adapter', label: '适配器', type: 'text', placeholder: 'adapter.qq#qq-1' },
          { name: 'conversation', label: '平台会话', type: 'text', placeholder: 'group-123456' },
        ],
        hint: '一个绑定 = 一个适配器上的一个会话。没有绑定的频道能建，但谁也投不进来。',
        required: false,
      },
    ],
    submit: '创建',
    onSubmit: async (values, dialog) => {
      const bindings = (values.bindings ?? []).filter((binding) => binding.adapter && binding.conversation);
      const window = String(values.maxMessages ?? '').trim();
      await perform(ctx, {
        action: 'channels.create',
        args: {
          type: values.type,
          instance: values.instance,
          name: values.name || undefined,
          maxMessages: window ? Number(window) : undefined,
          bindings: bindings.length ? bindings : undefined,
        },
        done: `${values.type}#${values.instance} 已创建`,
      });
      dialog.close();
    },
  });
}

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
    const create = h('button', {
      class: 'act',
      type: 'button',
      text: '新建频道',
      disabled: !ctx.allowed('channels.create'),
      title: ctx.allowed('channels.create') ? '' : '缺少能力 channels.control',
      onclick: () => openCreate(ctx),
    });

    root.append(panel(`频道（${rows.length}）`,
      dataTable({ columns: columns(ctx), rows, empty: '还没有频道。' })),
      h('div', { class: 'toolbar' }, create));
  },
};
