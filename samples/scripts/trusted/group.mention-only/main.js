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
    //
    // What the model may put in a message is spelled out here, and it has to be: this schema is the whole of
    // what a provider tells the model the tool takes. A block this file forgot is a block no model can ever
    // emit — which is how "send the file to the group" ended up impossible while the runtime supported it
    // (docs/28 §6.6). `text` and `blocks` are alternatives, so neither is required on its own.
    tools: [{
      name: "speak",
      description: "Send a message to this group. Nothing written outside a tool call reaches anyone.",
      parametersJson: JSON.stringify({
        type: "object",
        properties: {
          text: { type: "string", description: "What to say, as one run of text. Use this OR blocks, not both." },
          mode: {
            type: "string",
            enum: ["segments", "text"],
            description: "segments: one message per blank-line separated paragraph (default). text: the whole text as one message."
          },
          // A layout for one message: what a chat window shows as one bubble — and the only way to mention
          // somebody, send a picture, or hand a file over. A file is not a picture on every platform: QQ
          // uploads it separately and the chat never mentions it, which the receipt then says out loud.
          blocks: {
            type: "array",
            maxItems: 32,
            description: "One message laid out in order: words, mentions, pictures, faces, files. Use this OR text, not both.",
            items: {
              type: "object",
              properties: {
                type: { type: "string", enum: ["text", "at", "image", "face", "file"] },
                text: { type: "string", description: "for text" },
                who: { type: "string", description: "for at: \"sender\" (the person being answered) or a numeric platform id. \"all\" is refused." },
                path: { type: "string", description: "for image / file: media/<file> or workspace/<file> — something already stored." },
                scope: { type: "string", description: "for image / file, when the scope is given apart from path" },
                name: { type: "string", description: "for file: what the other side should see it called (defaults to the file's own name), at most 120 characters." },
                id: { type: "string", description: "for face: this platform's own emoji id, e.g. \"1\"" }
              },
              required: ["type"],
              additionalProperties: false
            }
          }
        },
        anyOf: [{ required: ["text"] }, { required: ["blocks"] }],
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
