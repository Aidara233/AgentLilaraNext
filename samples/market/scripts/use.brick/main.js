function main() { return null; }

async function use_brick(args) {
  return { text: "砖还是那块砖。", used: 0 };
}

function describe() {
  return { name: "use.brick", description: "砖的使用脚本（用不掉）", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}
