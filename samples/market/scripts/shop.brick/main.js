function main() { return null; }

async function box_brick(args) {
  return { text: "一块砖，沉甸甸的。", bag: [{ item: "test-brick", count: 1 }] };
}

function describe() {
  return { name: "shop.brick", description: "砖的结算脚本", parametersJson: "{\"type\":\"object\",\"properties\":{}}", access: "full" };
}
