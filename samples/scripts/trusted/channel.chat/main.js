// Minimal chat policy: every accepted message in this Channel runs one session turn, and the answer
// is sent back into the same Channel.
//
// It is a sample, not a builtin: which model, which prompt, how many messages to ignore, whether to
// answer at all — those are policy, and policy belongs in the data root where a human can read it.
// The timeout is generous because one turn is one model call; maxStatements is small because the
// script itself does almost nothing.
async function onEvent(event, ctx) {
  const spec = {
    modelProfileId: "default",
    objective: "在群聊里和成员对话",
    formatVersion: "1",
    systemPrompt: [
      "你在一个 QQ 群里和成员对话。",
      "回复会原样发到群里：不要使用 Markdown 标题、列表符号或代码块围栏，用适合聊天的短句。",
      "消息里形如 [图片]、[表情]、[语音] 的方括号内容是你看不到的附件占位符，不要假装看懂了它。",
      "不知道就说不确定，不要编造群里的信息。",
    ].join("\n"),
  };

  // userInput must be the event text verbatim: the session layer rejects a mismatch on purpose, so
  // that a script cannot quietly feed the model something other than what arrived.
  const outcome = JSON.parse(await ctx.session.run(spec, event.text));
  if (!outcome.ok) {
    ctx.log.error("session turn failed", { error: JSON.stringify(outcome.error ?? null) });
    return;
  }

  const text = outcome.result && outcome.result.text;
  if (!text) {
    ctx.log.warn("session produced no text; nothing to send", { sessionId: outcome.result?.sessionId ?? "" });
    return;
  }

  await ctx.channel.reply({ platformConversationId: event.binding.platformConversationId, text });
}
