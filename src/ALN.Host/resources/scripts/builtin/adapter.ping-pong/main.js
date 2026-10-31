async function onEvent(event, ctx) {
  if (event.text !== "ping") return;
  await ctx.self.adapter.send({
    platformConversationId: event.platformConversationId,
    text: "pong",
    replyTo: event.platformMessageId
  });
}
