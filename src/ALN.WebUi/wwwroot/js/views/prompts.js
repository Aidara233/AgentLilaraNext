/* Prompts: files under prompts/<group>/<name>.md. The page is an editor, because that is what a prompt
   is — the shipped copy is a default, and the moment somebody edits one, the data root's copy is the one
   that answers.

   `prompts.get` shows which file the text came from, so "which copy am I editing" is never a guess. */

import { h, panel, dataTable, note } from '../ui.js';
import { perform } from './shared.js';
import { openForm } from '../form.js';

const columns = (ctx, group) => [
  { title: '名称', class: 'mono', cell: (row) => row.name },
  { title: '字节', class: 'num', cell: (row) => row.bytes },
  { title: '指纹', class: 'mono', cell: (row) => row.fingerprint },
  { title: '文件', class: 'mono muted', cell: (row) => row.path },
  {
    title: '',
    class: 'actions',
    cell: (row) => h('button', {
      class: 'act',
      type: 'button',
      text: ctx.allowed('prompts.set') ? '编辑' : '查看',
      disabled: !ctx.allowed('prompts.get'),
      title: ctx.allowed('prompts.get') ? '' : '缺少能力 prompts.read',
      onclick: () => openEdit(ctx, group, row.name),
    }),
  },
];

async function openEdit(ctx, group, name) {
  let detail;
  try {
    detail = await ctx.api.invoke('prompts.get', { group, name });
  } catch (error) {
    ctx.notify(`读取失败：${error.message}`, 'bad');
    return;
  }

  const canWrite = ctx.allowed('prompts.set');
  openForm({
    title: `编辑 ${group}/${name}`,
    hint: detail.path,
    fields: [{
      name: 'text',
      label: '内容（Markdown）',
      type: 'textarea',
      rows: 18,
      value: detail.text ?? '',
      required: true,
      hint: `现在是 ${detail.bytes} 字节，上限 64 KB。空文件会被拒绝：那几乎总是没写完。`,
    }],
    submit: canWrite ? '保存' : '（只读）',
    onSubmit: async (values, dialog) => {
      if (!canWrite) return dialog.close();
      const saved = await perform(ctx, {
        action: 'prompts.set',
        args: { group, name, text: values.text },
        done: `${group}/${name} 已保存`,
      });
      if (saved) dialog.close();
    },
  });
}

export const prompts = {
  id: 'prompts',
  title: '提示词',
  hint: '保存写的是数据根里的那份（后读的覆盖先读的），所以改完下一轮就生效。',

  async mount(root, ctx) {
    if (!ctx.allowed('prompts.list')) {
      root.append(ctx.noPermission('prompts.list'));
      return;
    }
    const listed = (await ctx.api.invoke('prompts.list')) ?? {};
    const groups = listed.groups ?? [];
    if (!groups.length) {
      root.append(panel('提示词', note('还没有提示词文件。')));
      return;
    }
    for (const group of groups) {
      root.append(panel(`${group.group}（${group.prompts.length}）`,
        dataTable({ columns: columns(ctx, group.group), rows: group.prompts, empty: '这个组里没有文件。' })));
    }
    root.append(panel('查找顺序', note(`从低到高：${(listed.roots ?? []).join(' → ') || '（未知）'}。后面覆盖前面。`)));
  },
};
