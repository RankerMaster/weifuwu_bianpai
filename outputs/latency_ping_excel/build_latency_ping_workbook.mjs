import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const outputDir = "E:/vs_projects/微服务容器编排/远程服务器连接_plus/outputs/latency_ping_excel";
const outputPath = path.join(outputDir, "容器间最小通信延迟测试结果记录.xlsx");

const workbook = Workbook.create();
const record = workbook.worksheets.add("测试记录");
const method = workbook.worksheets.add("测试方法");

record.getRange("A1:K1").values = [[
  "测试日期",
  "宿主机型号",
  "容器网络",
  "发送端容器",
  "接收端容器",
  "发送次数",
  "接收次数",
  "丢包率",
  "RTT_min(ms)",
  "最小单向时延(ms)",
  "最小单向时延(us)",
]];

record.getRange("A2:K2").values = [[
  "2026-06-05",
  "PowerEdge R750xs",
  "latency-test",
  "latency-c1",
  "latency-c2",
  1000,
  1000,
  "0%",
  0.026,
  null,
  null,
]];
record.getRange("J2:J20").formulas = Array.from({ length: 19 }, (_, index) => [`=IF(I${index + 2}="","",I${index + 2}/2)`]);
record.getRange("K2:K20").formulas = Array.from({ length: 19 }, (_, index) => [`=IF(J${index + 2}="","",J${index + 2}*1000)`]);

record.getRange("A4:B10").values = [
  ["Ping 命令", "docker exec latency-c1 ping -c 1000 latency-c2"],
  ["计算公式", "最小单向通信时延 ≈ RTT_min / 2"],
  ["当前示例 RTT_min", "0.026 ms"],
  ["当前示例最小单向时延", "0.013 ms"],
  ["当前示例换算结果", "13 us"],
  ["说明", "Ping 测量的是往返时延 RTT，单向时延采用 RTT_min / 2 近似。"],
  ["建议", "验收时除最小值外，建议同时记录 avg、max、P95、P99。"],
];

method.getRange("A1:B1").values = [["项目", "内容"]];
method.getRange("A2:B16").values = [
  ["指标名称", "容器间最小通信延迟"],
  ["指标含义", "两容器间数据传输的单向时延"],
  ["测试对象", "高实时高可靠虚拟控制器"],
  ["测试方法", "使用 ICMP Ping 测量两个容器之间的往返时延 RTT"],
  ["创建网络", "docker network create latency-test"],
  ["启动容器 1", "docker run -dit --name latency-c1 --network latency-test alpine sh"],
  ["启动容器 2", "docker run -dit --name latency-c2 --network latency-test alpine sh"],
  ["安装 Ping 工具", "docker exec latency-c1 sh -c \"apk add --no-cache iputils\""],
  ["执行测试", "docker exec latency-c1 ping -c 1000 latency-c2"],
  ["结果读取", "读取 rtt min/avg/max/mdev 中的 min 值"],
  ["计算方法", "最小单向通信时延 ≈ RTT_min / 2"],
  ["示例", "RTT_min = 0.026 ms，则最小单向时延 = 0.013 ms = 13 us"],
  ["注意事项 1", "Ping 测量的是往返时延，不是严格意义上的单向时延"],
  ["注意事项 2", "如果需要严格单向时延，应保证两端时钟高度同步"],
  ["注意事项 3", "最小值只能说明理想最低开销，实时可靠性还应关注 avg、max、P95、P99"],
];

for (const sheet of [record, method]) {
  sheet.getRange("A1:K1").format.fill = "#FFC000";
  sheet.getRange("A1:K1").format.font = { bold: true, color: "#000000" };
  sheet.getRange("A1:K20").format.wrapText = true;
}

record.getRange("A1:K20").format.borders = { preset: "all", style: "thin", color: "#808080" };
method.getRange("A1:B16").format.borders = { preset: "all", style: "thin", color: "#808080" };

record.getRange("A:A").format.columnWidthPx = 110;
record.getRange("B:B").format.columnWidthPx = 145;
record.getRange("C:E").format.columnWidthPx = 125;
record.getRange("F:H").format.columnWidthPx = 95;
record.getRange("I:I").format.columnWidthPx = 110;
record.getRange("J:K").format.columnWidthPx = 145;
method.getRange("A:A").format.columnWidthPx = 145;
method.getRange("B:B").format.columnWidthPx = 620;

record.getRange("I2:K20").format.numberFormat = "0.000";
record.getRange("F2:G20").format.numberFormat = "0";
record.getRange("A1:K1").format.horizontalAlignment = "center";
method.getRange("A1:B1").format.horizontalAlignment = "center";

await fs.mkdir(outputDir, { recursive: true });
const output = await SpreadsheetFile.exportXlsx(workbook);
await output.save(outputPath);

console.log(outputPath);
