// The reason a plugin folder carries scripts: a module is machinery, a script is behaviour, and a plugin that
// only ships machinery is not usable until somebody writes the behaviour.
//
// This one is manual on purpose — mount it to see it:
//   graph.mount { "script": "sample.clock", "scope": "type", "selector": "channel.conversation" }
// It answers with the host's clock, which is the one thing a model cannot know by itself.
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;
  if (!String(event.text ?? "").includes("几点")) return;

  const now = new Date();
  const pad = (value) => String(value).padStart(2, "0");
  await ctx.channel.reply({
    platformConversationId: event.binding.platformConversationId,
    text: `宿主时间：${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ` +
          `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`
  });
}
