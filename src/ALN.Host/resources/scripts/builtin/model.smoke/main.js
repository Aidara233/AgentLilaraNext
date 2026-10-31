async function onEvent(event, ctx) {
  if (ctx.eventName !== "adapter.envelope" || !event.text.startsWith("/model ")) return;

  const prompt = event.text.slice("/model ".length).trim();
  if (prompt.length === 0) return;

  const raw = await ctx.model.complete("default", {
    messages: [{ role: "user", content: prompt }]
  });
  const result = JSON.parse(raw);
  const answer = result.events
    .filter(item => item.type === "textDelta")
    .map(item => item.text)
    .join("");
  const failure = result.events.find(item => item.type === "failed");

  await ctx.self.adapter.send({
    platformConversationId: event.platformConversationId,
    text: result.ok ? (answer || "模型未返回文本。") : `模型调用失败：${failure?.error?.message || result.error?.message || "未知错误"}`,
    replyTo: event.platformMessageId
  });
}
