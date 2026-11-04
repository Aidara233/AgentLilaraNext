/* Adapters: the mailbox side. Listing, start/stop, and — since adapters.create exists now — the form
   that makes one.

   The form asks for a type and an instance, not for an address: the address is derived from the two,
   which is what stops a spec from calling itself one thing and being another. */

import { panel, dataTable, stateBadge, badge, h } from '../ui.js';
import { actionButton, perform } from './shared.js';
import { openForm } from '../form.js';

const directions = [
  { value: 'both', label: '双向（both）' },
  { value: 'inbound', label: '只收（inbound）' },
  { value: 'outbound', label: '只发（outbound）' },
];

const columns = (ctx) => [
  { title: '地址', class: 'mono', cell: (row) => row.address },
  { title: '类型', class: 'mono', cell: (row) => row.typeId ?? '—' },
  { title: '平台', cell: (row) => row.platform },
  { title: '账号', class: 'mono', cell: (row) => row.account },
  { title: '方向', cell: (row) => badge(row.direction ?? '—') },
  { title: '状态', cell: (row) => stateBadge(row.state) },
  {
    title: '',
    class: 'actions',
    cell: (row) => {
      const running = /running/i.test(row.state ?? '');
      return h('span', { class: 'actions' },
        running
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
          }),
        actionButton(ctx, {
          action: 'adapters.destroy',
          args: { address: row.address },
          label: '删除',
          danger: true,
          ask: `删除 ${row.address}？规格文件会一并删掉，这个账号的连接配置就没了。`,
          done: `${row.address} 已删除`,
        }));
    },
  },
];

/** The concrete adapter types this process can build, from `system.types`. */
async function adapterTypes(api) {
  try {
    const types = (await api.invoke('system.types'))?.types ?? [];
    return types.filter((type) => !type.abstract && String(type.typeId).startsWith('adapter.'));
  } catch {
    // A viewer may read adapters without being allowed to list types; the form then offers a text box.
    return [];
  }
}

async function openCreate(ctx) {
  const types = await adapterTypes(ctx.api);
  const typeOptions = types.map((type) => ({ value: type.typeId, label: type.typeId }));
  openForm({
    title: '新建适配器',
    hint: '地址由类型和实例名拼出来：类型 adapter.file + 实例 file-2 → adapter.file#file-2。',
    fields: [
      typeOptions.length
        ? { name: 'type', label: '类型', type: 'select', options: typeOptions, value: typeOptions[0].value, required: true }
        : { name: 'type', label: '类型', type: 'text', placeholder: 'adapter.file', required: true },
      { name: 'instance', label: '实例名', type: 'text', placeholder: 'file-2', required: true, hint: '同一类型下唯一；也是目录名与地址里的实例段。' },
      { name: 'platform', label: '平台', type: 'text', placeholder: '留空 = 类型里的那一段（file / qq）' },
      { name: 'account', label: '账号', type: 'text', placeholder: '留空 = 实例名' },
      { name: 'direction', label: '方向', type: 'select', options: directions, value: 'both' },
      {
        name: 'settings',
        label: '设置',
        type: 'rows',
        addLabel: '添加一项设置',
        blank: () => ({ key: '', value: '' }),
        columns: [
          { name: 'key', label: '键', type: 'text' },
          { name: 'value', label: '值', type: 'text' },
        ],
        hint: '值只能是字符串——一个对象存进去会被下一个读者读成 [object Object]。',
      },
    ],
    submit: '创建',
    onSubmit: async (values, dialog) => {
      const settings = {};
      for (const row of values.settings ?? []) {
        if (!row.key) continue;
        settings[row.key] = row.value;
      }
      await perform(ctx, {
        action: 'adapters.create',
        args: {
          type: values.type,
          instance: values.instance,
          platform: values.platform || undefined,
          account: values.account || undefined,
          direction: values.direction,
          settings: Object.keys(settings).length ? settings : undefined,
        },
        done: `${values.type}#${values.instance} 已创建（未启动）`,
      });
      dialog.close();
    },
  });
}

export const adapters = {
  id: 'adapters',
  title: '适配器',
  hint: '启动/停止只影响本进程内的连接状态；新建与删除改的是磁盘上的规格。',

  async mount(root, ctx) {
    if (!ctx.allowed('adapters.list')) {
      root.append(ctx.noPermission('adapters.list'));
      return;
    }
    const rows = (await ctx.api.invoke('adapters.list')) ?? [];
    const create = h('button', {
      class: 'act',
      type: 'button',
      text: '新建适配器',
      disabled: !ctx.allowed('adapters.create'),
      title: ctx.allowed('adapters.create') ? '' : '缺少能力 adapters.control',
      onclick: () => openCreate(ctx),
    });

    root.append(panel(`适配器（${rows.length}）`,
      dataTable({ columns: columns(ctx), rows, empty: '还没有适配器。' })),
      h('div', { class: 'toolbar' }, create));
  },
};
