async function onEvent(event, ctx) {
  await ctx.channel.reply({
    platformConversationId: event.binding.platformConversationId,
    text: event.text,
    replyTo: event.platformMessageId
  });
}
