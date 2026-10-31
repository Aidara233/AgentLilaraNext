async function onEvent(event, ctx) {
  if (ctx.eventName !== "adapter.envelope") return;

  if (event.text.startsWith("/agent ")) {
    const prompt = event.text.slice("/agent ".length).trim();
    if (prompt.length === 0) return;

    const spec = {
      modelProfileId: "default",
      objective: "Respond helpfully to messages in this conversation.",
      systemPrompt: "You are ALN, a helpful assistant. Answer clearly and concisely. Use send_message when the user asks you to send a message to this conversation.",
      formatVersion: "1",
      tools: [{
        name: "send_message",
        description: "Send a text message to the current conversation.",
        parametersJson: JSON.stringify({
          type: "object",
          properties: { text: { type: "string", description: "Message text to send." } },
          required: ["text"],
          additionalProperties: false
        })
      }]
    };
    const raw = await ctx.session.run(spec, prompt);
    const response = JSON.parse(raw);
    const text = response.ok ? response.result.text : `Session failed: ${response.error?.message || "unknown error"}`;
    await ctx.self.adapter.send({
      platformConversationId: event.platformConversationId,
      text: text || "The model returned no text.",
      replyTo: event.platformMessageId
    });
    return;
  }

  if (!event.text.startsWith("/model ")) return;
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
