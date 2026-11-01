// A valve, not a pipe. It decides whether this message is worth waking the Session for, and it carries
// no content: the messages the model sees are pulled by the Session when it assembles the request, so a
// batch of messages becomes one turn instead of N turns, and the model never sees a half-conversation.
//
// Mounted by hand ("mount": "manual") — which channel gets which policy is a decision, not a default.
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;

  const spec = {
    // No modelProfileId: the engine resolves it (the "default" profile if one exists, otherwise the first
    // registered one) and says which it used. Pinning a provider belongs in configuration, not in a script.
    objective: "在群聊或私聊里和对方对话",
    formatVersion: "1",
    systemPrompt: [
      "你在一个 QQ 会话里和对方对话。",
      "回复会原样发出去：不要用 Markdown 标题、列表符号或代码块围栏，用适合聊天的短句。",
      "消息里形如 [图片]、[表情]、[语音] 的方括号内容是你看不到的附件占位符，不要假装看懂了它。",
      "不知道就说不确定，不要编造对方没说过的信息。",
    ].join("\n"),
  };

  // No content is passed on purpose: the Session pulls what it has not seen yet.
  const outcome = JSON.parse(await ctx.session.activate(spec));
  if (!outcome.ok) {
    ctx.log.error("session activation failed", { error: JSON.stringify(outcome.error ?? null) });
    return;
  }

  const text = outcome.result && outcome.result.text;
  if (!text) {
    ctx.log.warn("turn produced no text; nothing to send", { sessionId: outcome.result?.sessionId ?? "" });
    return;
  }

  await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text });
}
