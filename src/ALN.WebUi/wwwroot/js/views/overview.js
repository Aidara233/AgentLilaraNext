/* Overview. One action call, then two honest panels: what the identity is, and what this console
   still cannot do. The second one exists because a UI that quietly lacks a feature looks like the
   core lacks it. */

import { h, stat, panel, kv, note, badge } from '../ui.js';

export const overview = {
  id: 'overview',
  title: '概览',
  hint: '核心自报的状态。数字来自 system.info 动作——界面不读数据目录，也不缓存核心状态。',

  async mount(root, ctx) {
    if (!ctx.allowed('system.info')) {
      root.append(ctx.noPermission('system.info'));
      return;
    }

    const info = await ctx.api.invoke('system.info');
    root.append(h('div', { class: 'stats' },
      stat('适配器', info.adapters ?? 0, 'adapters.list 的条目数'),
      stat('频道', info.channels ?? 0, 'channels.list 的条目数'),
      stat('模型 Profile', info.profiles ?? 0, '可用的模型配置数')),
      h('div', { class: 'stats' },
        stat('运行中', info.running ? '是' : '否', info.core ?? ''),
        stat('动作', ctx.actions.length, `${ctx.actions.filter((a) => a.allowed).length} 个当前身份允许`),
        stat('变更类动作', ctx.actions.filter((a) => a.mutating).length, '会改状态，均需确认')));

    root.append(panel('当前身份', kv([
      ['id', ctx.actor?.id ?? '?'],
      ['类型', ctx.actor?.kind ?? '?'],
      ['能力', badge(`${ctx.actions.filter((a) => a.allowed).length} / ${ctx.actions.length} 个动作`, 'info')],
    ])));

    root.append(panel('这个界面还没有做的事',
      note('会话与消息的读写、流式对话（sessions.turn / model.complete）、创建或修改 Adapter 与 Channel 的写入动作，都还没有对应的网络动作。', 'info'),
      h('p', { class: 'hint' }, '动作目录页可以直接调用任何已注册的动作，包括尚未做专用表单的那些。'),
      h('p', { class: 'hint' }, '适配器与频道的「启动/停止」是这里能做到的全部控制；创建仍然走脚本侧的 channel.route。')));
  },
};
