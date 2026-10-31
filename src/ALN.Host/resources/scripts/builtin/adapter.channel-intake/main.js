async function onEvent(event, ctx) {
  if (ctx.eventName === "clock.pulse") {
    await ctx.self.adapter.poll();
    return;
  }
  await ctx.channel.accept();
}
