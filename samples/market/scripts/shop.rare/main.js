function main() { return null; }

async function box_rare(args) {
  return { text: "柜台上最后一个，归你了。", bag: [{ item: "test-note", count: 1 }] };
}

function describe() {
  return { name: "shop_rare", description: "稀罕物的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}
