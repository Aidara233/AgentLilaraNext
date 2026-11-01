// Group policy: speak only when spoken to.
//
// The valve lives here, in the script mounted on this one Channel — not in the Channel's type and not in
// its id. `mentionsSelf` is decided by the Adapter (it is the only layer that knows our own account id),
// so "@全体成员" does not count as an address to us.
//
// Mount it on a group channel instead of channel.chat:
//   graph.mount { "script": "group.mention-only", "target": "channel.conversation#my-group" }
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;
  if (!event.mentionsSelf) return;

  const spec = {
    modelProfileId: "default",
    objective: "在这个群里参与对话",
    formatVersion: "1",
    systemPrompt: [
      "你在一个 QQ 群里，只在被点名时说话。",
      "回复会原样发到群里：短句，不要 Markdown 标题或代码块围栏。",
      "消息里形如 [图片]、[表情] 的方括号内容是你看不到的附件占位符，不要假装看懂了它。",
    ].join("\n"),
  };

  const outcome = JSON.parse(await ctx.session.activate(spec));
  if (!outcome.ok) {
    ctx.log.error("session activation failed", { error: JSON.stringify(outcome.error ?? null) });
    return;
  }
  const text = outcome.result && outcome.result.text;
  if (text) await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text });
}
