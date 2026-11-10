function main() { return null; }

async function use_note(args) {
  return { text: "上面写着：周三之前给他那个工具。", used: 0 };
}

function describe() {
  return { name: "use.note", description: "便签的使用脚本（不消耗）", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}
