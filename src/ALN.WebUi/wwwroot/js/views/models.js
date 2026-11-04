/* Models: provider instances (a file each) and the profiles that point at them.

   Two behaviours worth knowing while reading this file:
   - the key never comes back. `models.provider.get` returns it as ***, and saving that *** back means
     "keep the stored one" — so the edit form can be opened and saved without touching the secret;
   - a provider that is not running is built on save ("applies now"), one that is running says
     "restart". Saying so is better than pretending an in-flight provider can swap its own config. */

import { h, panel, dataTable, badge, note } from '../ui.js';
import { perform } from './shared.js';
import { openForm } from '../form.js';

const providerColumns = (ctx) => [
  { title: '地址', class: 'mono', cell: (row) => row.address },
  { title: '类型', class: 'mono', cell: (row) => row.typeId ?? '—' },
  {
    title: '状态',
    cell: (row) => (row.running ? badge('运行中', 'ok') : badge('未加载', '')),
  },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('span', { class: 'actions' },
      h('button', {
        class: 'act',
        type: 'button',
        text: '编辑',
        disabled: !ctx.allowed('models.provider.get'),
        title: ctx.allowed('models.provider.get') ? '' : '缺少能力 models.read',
        onclick: () => openEdit(ctx, row),
      }),
      h('button', {
        class: 'act danger',
        type: 'button',
        text: '删除',
        disabled: !ctx.allowed('models.provider.destroy'),
        title: ctx.allowed('models.provider.destroy') ? '' : '缺少能力 models.control',
        onclick: async () => {
          if (!window.confirm(`删除 ${row.address}？配置文件会一并删除；如果还有 profile 指向它，删除会被拒绝。`)) return;
          await perform(ctx, {
            action: 'models.provider.destroy',
            args: { address: row.address },
            done: `${row.address} 已删除`,
          });
        },
      })),
  },
];

const profileColumns = (ctx) => [
  { title: 'id', class: 'mono', cell: (row) => row.id },
  { title: 'providers', class: 'mono', cell: (row) => row.provider },
  { title: 'model', class: 'mono', cell: (row) => row.model },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act danger',
      type: 'button',
      text: '删除',
      disabled: !ctx.allowed('models.profile.remove'),
      title: ctx.allowed('models.profile.remove') ? '' : '缺少能力 models.control',
      onclick: async () => {
        if (!window.confirm(`删除 profile ${row.id}？用到它的频道会在下一轮退回宿主默认解析。`)) return;
        await perform(ctx, { action: 'models.profile.remove', args: { id: row.id }, done: `profile ${row.id} 已删除` });
      },
    }),
  },
];

async function openEdit(ctx, row) {
  const detail = await ctx.api.invoke('models.provider.get', { address: row.address });
  if (!detail?.found) {
    ctx.notify(`${row.address} 没有配置文件，只有运行实例。`, 'bad');
    return;
  }
  const profiles = detail.profiles ?? [];
  openForm({
    title: `编辑 ${row.address}`,
    hint: detail.running
      ? '这个 provider 正在运行：保存会写文件，但要重启核心才生效。'
      : '这个 provider 没有在运行：保存时会被创建，立即生效。',
    fields: [
      {
        name: 'configuration',
        label: '配置（JSON）',
        type: 'textarea',
        rows: 12,
        value: JSON.stringify(detail.configuration ?? {}, null, 2),
        hint: '密钥读出来是 ***；保持 *** 不动就是保留原来的值。',
        required: true,
      },
      { name: 'enabled', label: '启用', type: 'checkbox', value: detail.enabled !== false },
    ],
    submit: '保存',
    onSubmit: async (values, dialog) => {
      let configuration;
      try {
        configuration = JSON.parse(values.configuration);
      } catch (error) {
        dialog.fail(`配置不是合法 JSON：${error.message}`);
        return;
      }
      const saved = await perform(ctx, {
        action: 'models.provider.save',
        args: {
          address: row.address,
          configuration,
          enabled: values.enabled,
          profiles: profiles.map((profile) => ({ id: profile.id, model: profile.model })),
        },
        done: '已保存',
      });
      if (saved) dialog.close();
    },
  });
}

async function openCreate(ctx, types) {
  const categories = [...new Map(types.map((type) => [type.category, type])).values()];
  const options = categories.length
    ? categories.map((type) => ({ value: type.category, label: `${type.category}（${type.typeId}）` }))
    : [{ value: 'chat', label: 'chat' }];

  openForm({
    title: '新建模型 provider',
    hint: '一个 provider 一个文件：models/<category>/<instance>.json。地址由 category 推出，例如 chat + deepseek → model.openai-chat#deepseek。',
    fields: [
      { name: 'category', label: '类别', type: 'select', options, value: options[0].value, required: true },
      { name: 'instance', label: '实例名', type: 'text', placeholder: 'deepseek_official', required: true },
      {
        name: 'configuration',
        label: '配置（JSON）',
        type: 'textarea',
        rows: 10,
        value: JSON.stringify({ endpoint: 'https://api.example.com', apiKey: '' }, null, 2),
        hint: '字段由 provider 类型决定；OpenAI 兼容的是 endpoint 与 apiKey。',
        required: true,
      },
    ],
    submit: '创建',
    onSubmit: async (values, dialog) => {
      let configuration;
      try {
        configuration = JSON.parse(values.configuration);
      } catch (error) {
        dialog.fail(`配置不是合法 JSON：${error.message}`);
        return;
      }
      const saved = await perform(ctx, {
        action: 'models.provider.save',
        args: { category: values.category, instance: values.instance, configuration },
        done: `${values.category}/${values.instance} 已保存`,
      });
      if (saved) dialog.close();
    },
  });
}

function openProfile(ctx, providers) {
  const options = providers.length
    ? providers.map((row) => ({ value: row.address, label: row.address }))
    : [{ value: '', label: '（还没有 provider）' }];
  openForm({
    title: '新建模型 profile',
    hint: '会话按 id 找 profile："default" 是宿主优先解析的那个。profile 会写进它所在 provider 的文件。',
    fields: [
      { name: 'id', label: 'id', type: 'text', placeholder: 'default', required: true },
      { name: 'provider', label: 'provider', type: 'select', options, value: options[0].value, required: true },
      { name: 'model', label: 'model', type: 'text', placeholder: 'deepseek-flash', required: true },
    ],
    submit: '创建',
    onSubmit: async (values, dialog) => {
      await perform(ctx, {
        action: 'models.profile.create',
        args: { id: values.id, provider: values.provider, model: values.model },
        done: `profile ${values.id} 已创建`,
      });
      dialog.close();
    },
  });
}

export const models = {
  id: 'models',
  title: '模型',
  hint: 'provider 是文件（models/<category>/<instance>.json），profile 是"哪个 provider + 哪个模型"的名字。',

  async mount(root, ctx) {
    if (!ctx.allowed('models.list')) {
      root.append(ctx.noPermission('models.list'));
      return;
    }
    const listed = (await ctx.api.invoke('models.list')) ?? {};
    const providers = listed.providers ?? [];
    const profiles = listed.profiles ?? [];
    const types = listed.types ?? [];

    const createProvider = h('button', {
      class: 'act',
      type: 'button',
      text: '新建 provider',
      disabled: !ctx.allowed('models.provider.save'),
      title: ctx.allowed('models.provider.save') ? '' : '缺少能力 models.control',
      onclick: () => openCreate(ctx, types),
    });
    const createProfile = h('button', {
      class: 'act',
      type: 'button',
      text: '新建 profile',
      disabled: !ctx.allowed('models.profile.create') || providers.length === 0,
      title: !ctx.allowed('models.profile.create') ? '缺少能力 models.control' : providers.length === 0 ? '先有 provider 才能建 profile' : '',
      onclick: () => openProfile(ctx, providers),
    });

    root.append(
      panel(`模型 provider（${providers.length}）`,
        dataTable({ columns: providerColumns(ctx), rows: providers, empty: '还没有 provider。' }),
        h('div', { class: 'toolbar' }, createProvider)),
      panel(`模型 profile（${profiles.length}）`,
        dataTable({ columns: profileColumns(ctx), rows: profiles, empty: '还没有 profile；没有 profile 的会话没法调用模型。' }),
        h('div', { class: 'toolbar' }, createProfile),
        note('改了配置里的密钥之后，正在运行的那个 provider 要重启核心才换成新的。', 'muted')));
  },
};
