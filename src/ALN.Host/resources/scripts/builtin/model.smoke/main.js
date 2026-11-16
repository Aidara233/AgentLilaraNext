// Builtin probes, and nothing else: this script answers only messages that start with an explicit
// prefix. It used to answer every message in every Channel, which meant that as soon as a real
// channel-bound policy existed, each message got two replies — one from the policy and one error from
// this script losing the session race. A probe that talks over the conversation is not a probe.
//
//   /model <prompt>   one model call, no session, no tools
//   /session <text>   one session turn with the send_message tool bound
async function onEvent(event, ctx) {
  if (ctx.eventName !== "channel.message.accepted") return;
  const text = String(event.text ?? "");

  if (text.startsWith("/model ")) {
    const prompt = text.slice("/model ".length).trim();
    if (prompt.length === 0) return;

    const raw = await ctx.model.complete("default", {
      messages: [{ role: "user", content: prompt }]
    });
    const result = JSON.parse(raw);
    const answer = result.events
      .filter(item => item.type === "textDelta")
      .map(item => item.text)
      .join("");
    const failure = result.events.find(item => item.type === "failed");

    await ctx.channel.reply({
      platformConversationId: event.binding.platformConversationId,
      text: result.ok ? (answer || "模型未返回文本。") : `模型调用失败：${failure?.error?.message || result.error?.message || "未知错误"}`,
      replyTo: event.platformMessageId
    });
    return;
  }

  if (text.startsWith("/session ")) {
    const prompt = text.slice("/session ".length).trim();
    if (prompt.length === 0) return;
    const spec = {
      modelProfileId: "default",
      objective: "Respond helpfully to messages in this conversation.",
      // Which prompt answers each group; the text itself lives in <dataRoot>/prompts (shipped defaults next
      // to the binary, the operator's copy on top). Editing a prompt must not mean editing a script.
      prompts: {
        "1身份": "标准", "2风格": "标准", "3社交": "标准",
        "4环境": "标准", "5安全": "标准",
      },
      formatVersion: "1",
      tools: [{
        name: "speak",
        description: "Send a message to this conversation. Nothing written outside a tool call reaches anyone.",
        parametersJson: JSON.stringify({
          type: "object",
          properties: {
            text: { type: "string", description: "Message text to send. Use this OR blocks, not both." },
            // The mode is a request, not a transport detail: "segments" sends each blank-line separated
            // paragraph as its own message (what a chat window wants), "text" keeps the whole block in one.
            mode: {
              type: "string",
              enum: ["segments", "text"],
              description: "segments: one message per blank-line separated paragraph (default). text: the whole text as one message."
            },
            // A layout for one message: what a chat window shows as one bubble. Blocks are the only way to
            // mention somebody, send a picture, or hand over a file; plain text stays plain text.
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
      }]
    };
    // The session layer requires the input to be the event text verbatim, so the prefix is stripped
    // into the prompt by the model seeing it, not by sending something different from what arrived.
    const raw = await ctx.session.run(spec, text);
    const response = JSON.parse(raw);
    if (!response.ok) {
      await ctx.channel.reply({
        platformConversationId: event.binding.platformConversationId,
        text: `Session failed: ${response.error?.message || "unknown error"}`,
        replyTo: event.platformMessageId
      });
      return;
    }
    // The answer went out through the speak tool. Text left over here means the model talked to nobody:
    // dropping it is the point of making speaking a tool, and the probe says so instead of quietly sending it.
    if (response.result.text) {
      ctx.log.warn("session produced text without calling speak; it was not sent", {
        text: response.result.text.slice(0, 200)
      });
    }
  }
}
