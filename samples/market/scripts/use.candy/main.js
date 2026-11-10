function main() { return null; }

async function use_candy(args) {
  return { text: "甜的，像纸板。", used: 1 };
}

function describe() {
  return { name: "use.candy", description: "糖的使用脚本（消耗一件）", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}
