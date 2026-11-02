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
    // The words live in <dataRoot>/prompts/<group>/<file>.md, not here: a prompt is something a person edits,
    // and an edit must not require touching a script (or restarting anything).
    prompts: { "人设": "标准人设", "安全": "标准安全" },
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
