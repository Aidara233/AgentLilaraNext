// Inbound policy for every Adapter, and deliberately the boring one: deliver the message to the
// Channel that already claims this conversation, or drop it.
//
// Channels are created by hand in this version (a spec file under <DataRoot>/channels). Nothing here
// invents one from an incoming message: a stranger's first message must not be able to make the core
// grow a Channel, bind an Adapter and start costing model calls on its own. The drop is logged so
// "why is the bot silent" has an answer in the signals page instead of a shrug.
async function onEvent(event, ctx) {
  const result = JSON.parse(await ctx.channel.accept());
  if (!result.acceptedChannels) {
    ctx.log.warn("no channel bound to this conversation; message dropped", {
      platformId: String(event.platformId ?? ""),
      conversation: String(event.platformConversationId ?? ""),
    });
  }
}
