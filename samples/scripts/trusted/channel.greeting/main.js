// Greet this conversation once, and only once — including across restarts.
//
// The memory is ctx.state, not a variable: the script is rebuilt for every event and the process may be
// killed between two of them. The default namespace is (script, target), so each Channel gets its own
// flag; mount the same script with "stateScope": "shared" if one greeting should cover all of them.
//
// The flag is written *before* the reply goes out, the same way the engine spends a mount budget before
// running the handler: a crash in between costs one greeting; a duplicate greeting is what we refuse.
//
//   graph.mount { "script": "channel.greeting", "scope": "type", "selector": "channel.conversation" }
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;

  if (await ctx.state.get("greeted")) return;
  await ctx.state.set("greeted", { at: new Date().toISOString() });

  await ctx.channel.reply({
    platformConversationId: event.binding.platformConversationId,
    text: "第一次在这儿说话，先打个招呼：有事直接说，我尽量简短地回。"
  });
}
