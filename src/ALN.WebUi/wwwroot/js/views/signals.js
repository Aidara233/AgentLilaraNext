/* Signals: the in-memory window, filtered on the server. The file log on disk is not read here —
   that would mean teaching the browser a second data source that can disagree with the action. */

import { h, clear, dataTable, levelBadge, badge, clock, duration, detailBlock, note } from '../ui.js';

const KNOWN_GROUPS = ['queue', 'dispatch', 'capability', 'session', 'model', 'module', 'lifecycle', 'control'];
const LIMITS = [100, 200, 500, 1000];

// Auto-refresh re-mounts the view every few seconds; keeping the filter here means the list doesn't
// jump back to "all groups, 200" under the hand of whoever is reading it.
let lastGroup = '';
let lastLimit = 200;

const columns = [
  { title: '时间', class: 'mono', cell: (row) => clock(row.at) },
  { title: '级别', cell: (row) => levelBadge(row.level) },
  { title: '分组', cell: (row) => badge(row.group ?? '—') },
  { title: '名称', class: 'mono', cell: (row) => row.name ?? '—' },
  { title: '结果', cell: (row) => badge(row.outcome ?? '—', /fail|error/i.test(row.outcome ?? '') ? 'bad' : '') },
  { title: '耗时', class: 'num', cell: (row) => duration(row.durationMs) },
  { title: '步骤', class: 'mono', cell: (row) => (row.step ? row.step.slice(0, 8) : '—') },
  { title: '详情', cell: (row) => detailBlock(row.detail) },
];

export const signals = {
  id: 'signals',
  title: '信号',
  hint: '只读内存里的最近记录；分组与条数在服务端过滤。', 

  async mount(root, ctx) {
    if (!ctx.allowed('signals.recent')) {
      root.append(ctx.noPermission('signals.recent'));
      return;
    }

    let group = lastGroup;
    let limit = lastLimit;
    const groups = new Set(KNOWN_GROUPS);

    const groupSelect = h('select', {
      onchange: (event) => { group = lastGroup = event.currentTarget.value; void load(); },
    });
    const limitSelect = h('select', {
      onchange: (event) => { limit = lastLimit = Number(event.currentTarget.value); void load(); },
    }, LIMITS.map((value) => h('option', { value: String(value), text: String(value) })));
    limitSelect.value = String(limit);

    const counter = h('span', { class: 'muted' });
    const host = h('div');
    const toolbar = h('div', { class: 'toolbar' },
      h('label', {}, '分组', groupSelect),
      h('label', {}, '条数', limitSelect),
      h('div', { class: 'spacer' }),
      counter);

    function renderGroups() {
      const keep = group;
      clear(groupSelect);
      groupSelect.append(h('option', { value: '', text: '全部' }));
      for (const name of [...groups].sort()) groupSelect.append(h('option', { value: name, text: name }));
      groupSelect.value = keep;
    }

    async function load() {
      const args = { limit };
      if (group) args.group = group;
      const data = await ctx.api.invoke('signals.recent', args);
      const records = data?.records ?? [];
      for (const record of records) if (record.group) groups.add(record.group);
      renderGroups();
      counter.textContent = `${records.length} 条`;
      clear(host).append(dataTable({ columns, rows: records, empty: '这个窗口里没有记录。' }));
    }

    root.append(toolbar, host,
      note('按 StepId 拉出整条链路还没有动作；这里的「步骤」只是短 id，用于人工对照。日志文件在数据目录的 logs/ 下，界面不读它。', 'info'));

    renderGroups();
    await load();
  },
};
