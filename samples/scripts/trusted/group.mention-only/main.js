// Group policy: speak only when spoken to.
//
// The valve lives here, in the script mounted on this one Channel — not in the Channel's type and not in
// its id. `mentionsSelf` is decided by the Adapter (it is the only layer that knows our own account id),
// so "@全体成员" does not count as an address to us.
//
// Mount it on a group channel instead of channel.chat:
//   graph.mount { "script": "group.mention-only", "target": "channel.conversation#my-group" }
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;
  if (!event.mentionsSelf) return;

  const spec = {
    modelProfileId: "default",
    objective: "在这个群里参与对话",
    formatVersion: "1",
    // Shipped defaults, overridable file by file from the data root. "只在被点名时说话" is not here: that is
    // the policy of this script (mentionsSelf), and a prompt is not the right place to hide a decision.
    prompts: {
      "1身份": "标准", "2风格": "标准", "3社交": "标准",
      "4环境": "标准", "5安全": "标准",
    },
    // Speaking is a tool: the only text that reaches the group is text the model chose to send. `wait` is
    // always available and is how a turn ends.
    tools: [{
      name: "speak",
      description: "Send a message to this group. Nothing written outside a tool call reaches anyone.",
      parametersJson: JSON.stringify({
        type: "object",
        properties: {
          text: { type: "string", description: "What to say." },
          mode: {
            type: "string",
            enum: ["segments", "text"],
            description: "segments: one message per blank-line separated paragraph (default). text: the whole text as one message."
          }
        },
        required: ["text"],
        additionalProperties: false
      })
    }],
  };

  const outcome = JSON.parse(await ctx.session.activate(spec));
  if (!outcome.ok) {
    ctx.log.error("session activation failed", { error: JSON.stringify(outcome.error ?? null) });
    return;
  }
}
