async function onEvent(event, ctx) {
  if (ctx.eventName === "clock.pulse") {
    await ctx.self.adapter.poll();
    return;
  }
  // The script owns channel policy: reuse the channel bound to this conversation, or create one.
  await ctx.channel.route({ channelId: event.platformConversationId });
}
