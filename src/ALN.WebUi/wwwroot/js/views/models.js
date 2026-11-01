/* Models: which providers exist and which profiles can be called. Deliberately thin — the action
   returns addresses and type ids only, because a provider's configuration is where the api key lives.
   Nothing here can show a key, and nothing here needs one. */

import { panel, dataTable, note } from '../ui.js';

export const models = {
  id: 'models',
  title: '模型',
  hint: '只有地址与类型：Provider 配置（含密钥）不属于网络返回值。',

  async mount(root, ctx) {
    if (!ctx.allowed('models.list')) {
      root.append(ctx.noPermission('models.list'));
      return;
    }
    const data = (await ctx.api.invoke('models.list')) ?? {};
    const providers = data.providers ?? [];
    const profiles = data.profiles ?? [];

    root.append(panel(`Provider 实例（${providers.length}）`,
      dataTable({
        columns: [{ title: '地址', class: 'mono', cell: (row) => row.address }],
        rows: providers,
        empty: '还没有 Provider 实例。',
      })));

    root.append(panel(`模型 Profile（${profiles.length}）`,
      dataTable({
        columns: [
          { title: 'id', class: 'mono', cell: (row) => row.id },
          { title: 'Provider', class: 'mono', cell: (row) => row.provider },
          { title: '模型', class: 'mono', cell: (row) => row.model },
        ],
        rows: profiles,
        empty: '还没有模型 Profile。',
      })));

    root.append(note('配置写入口令还没开放：修改 Provider 与 Profile 仍然要改 data root 下的 models/*.json，然后重启核心。', 'info'));
  },
};
