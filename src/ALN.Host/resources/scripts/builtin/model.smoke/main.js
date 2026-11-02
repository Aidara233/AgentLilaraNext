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
        name: "send_message",
        description: "Send a text message to the current conversation.",
        parametersJson: JSON.stringify({
          type: "object",
          properties: { text: { type: "string", description: "Message text to send." } },
          required: ["text"],
          additionalProperties: false
        })
      }]
    };
    // The session layer requires the input to be the event text verbatim, so the prefix is stripped
    // into the prompt by the model seeing it, not by sending something different from what arrived.
    const raw = await ctx.session.run(spec, text);
    const response = JSON.parse(raw);
    const reply = response.ok ? response.result.text : `Session failed: ${response.error?.message || "unknown error"}`;
    await ctx.channel.reply({
      platformConversationId: event.binding.platformConversationId,
      text: reply || "The model returned no text.",
      replyTo: event.platformMessageId
    });
  }
}
