// A valve, not a pipe. It decides whether this message is worth waking the Session for, and it carries
// no content: the messages the model sees are pulled by the Session when it assembles the request, so a
// batch of messages becomes one turn instead of N turns, and the model never sees a half-conversation.
//
// Mounted by hand ("mount": "manual") — which channel gets which policy is a decision, not a default.
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;

  const spec = {
    // No modelProfileId: the engine resolves it (the "default" profile if one exists, otherwise the first
    // registered one) and says which it used. Pinning a provider belongs in configuration, not in a script.
    objective: "在群聊或私聊里和对方对话",
    formatVersion: "1",
    // The words live in <dataRoot>/prompts/<group>/<file>.md, not here: a prompt is something a person edits,
    // and an edit must not require touching a script (or restarting anything).
    prompts: {
      "1身份": "标准", "2风格": "标准", "3社交": "标准",
      "4环境": "标准", "5安全": "标准",
    },
    // Speaking is a tool, so the only text that reaches the conversation is text the model chose to send.
    // A turn that ends without calling it has said nothing — that is the point, not an accident. `wait` is
    // always available and is how a turn ends, so it is not declared here.
    //
    // The schema below is the whole of what a provider tells the model this tool takes, so a block missing
    // from it is a block no model can emit. That is exactly how "send this file" became impossible on a
    // build whose runtime supported it all along (docs/28 §6.6); `text` and `blocks` are alternatives, so
    // neither of them is required on its own.
    tools: [{
      name: "speak",
      description: "Send a message to this conversation. Nothing written outside a tool call reaches anyone.",
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
          // somebody, send a picture, or hand a file over.
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

  // No content is passed on purpose: the Session pulls what it has not seen yet.
  const outcome = JSON.parse(await ctx.session.activate(spec));
  if (!outcome.ok) {
    ctx.log.error("session activation failed", { error: JSON.stringify(outcome.error ?? null) });
    return;
  }
}
