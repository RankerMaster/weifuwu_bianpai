import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const root = process.cwd();
const outputDir = path.join(root, "outputs");
await fs.mkdir(outputDir, { recursive: true });

const workbook = Workbook.create();
const summary = workbook.worksheets.add("说明与结论");
const log = workbook.worksheets.add("测试记录");
const rules = workbook.worksheets.add("判定规则");

summary.showGridLines = false;
log.showGridLines = false;
rules.showGridLines = false;

summary.getRange("A1:H1").merge();
summary.getRange("A1").values = [["业务容器同时运行数量测试模板"]];
summary.getRange("A1").format = {
  fill: "#1F4E5F",
  font: { bold: true, color: "#FFFFFF", size: 16 },
};

summary.getRange("A3:B11").values = [
  ["测试对象", "业务容器最大稳定同时运行数量"],
  ["业务镜像", ""],
  ["单容器基准耗时(ms)", 60],
  ["超时阈值(ms)", 500],
  ["每轮观察目标", "观察其中一个固定目标容器的运行结束耗时"],
  ["判定逻辑", "目标容器耗时 <= 500ms 且容器状态正常，则该轮通过"],
  ["最大稳定数量", ""],
  ["首次超阈值数量", ""],
  ["建议结论", ""],
];

summary.getRange("B9").formulas = [["=IFERROR(LOOKUP(2,1/('测试记录'!J5:J104=\"通过\"),'测试记录'!B5:B104),\"\")"]];
summary.getRange("B10").formulas = [["=IFERROR(INDEX('测试记录'!B5:B104,MATCH(\"不通过\",'测试记录'!J5:J104,0)),\"\")"]];
summary.getRange("B11").formulas = [["=IF(B9=\"\",\"尚无有效通过轮次\",IF(B10=\"\",\"当前已测范围内最大稳定同时运行数量为 \"&B9&\" 个，尚未触达 500ms 阈值。\",\"最大稳定同时运行数量建议记录为 \"&B9&\" 个；\"&B10&\" 个开始超过阈值或状态异常。\"))"]];

summary.getRange("A3:A11").format = {
  fill: "#D9EAF0",
  font: { bold: true },
};
summary.getRange("A3:B11").format = {
  borders: { insideHorizontal: { style: "Continuous", color: "#B7C9D1" }, insideVertical: { style: "Continuous", color: "#B7C9D1" }, edgeBottom: { style: "Continuous", color: "#B7C9D1" } },
  wrapText: true,
};
summary.getRange("B5:B6").format.numberFormat = "0";
summary.getRange("B9:B10").format.numberFormat = "0";

summary.getRange("D3:H8").values = [
  ["操作顺序", "", "", "", ""],
  ["1", "先填业务镜像、基准耗时和阈值。", "", "", ""],
  ["2", "每轮用 run 模式启动 N 个相同业务容器。", "", "", ""],
  ["3", "只观察固定目标容器的运行结束耗时，同时记录最终 running 数。", "", "", ""],
  ["4", "当目标容器耗时超过 500ms，或出现 exited/restarting，该轮判为不通过。", "", "", ""],
  ["5", "上一轮通过的数量就是最大稳定同时运行容器数量。", "", "", ""],
];
summary.getRange("D3:H3").merge();
summary.getRange("D4:H4").merge(true);
summary.getRange("D5:H5").merge(true);
summary.getRange("D6:H6").merge(true);
summary.getRange("D7:H7").merge(true);
summary.getRange("D8:H8").merge(true);
summary.getRange("D3").format = { fill: "#1F4E5F", font: { bold: true, color: "#FFFFFF" } };
summary.getRange("D4:H8").format = { fill: "#F5F8FA", wrapText: true };

log.getRange("A1:K1").merge();
log.getRange("A1").values = [["测试记录：每一行代表一轮并发数量测试"]];
log.getRange("A1").format = {
  fill: "#1F4E5F",
  font: { bold: true, color: "#FFFFFF", size: 14 },
};

log.getRange("A3:K3").values = [[
  "轮次",
  "目标同时运行数量",
  "成功启动数量",
  "最终running数量",
  "目标容器开始时间",
  "目标容器结束时间",
  "目标容器耗时(ms)",
  "是否超过500ms",
  "异常容器数",
  "判定",
  "备注",
]];

const sampleRows = [
  [1, 5, 5, 5, "", "", 63, "", 0, "", "示例：基准附近"],
  [2, 10, 10, 10, "", "", 88, "", 0, "", ""],
  [3, 20, 20, 20, "", "", 210, "", 0, "", ""],
  [4, 30, 30, 30, "", "", 480, "", 0, "", ""],
  [5, 40, 40, 39, "", "", 530, "", 1, "", "示例：超过阈值"],
];
log.getRange("A4:K8").values = sampleRows;

log.getRange("H4").formulas = [["=IF(G4=\"\",\"\",IF(G4>'说明与结论'!$B$6,\"是\",\"否\"))"]];
log.getRange("H4:H104").fillDown();
log.getRange("J4").formulas = [["=IF(OR(B4=\"\",G4=\"\"),\"\",IF(AND(C4=B4,D4=B4,H4=\"否\",I4=0),\"通过\",\"不通过\"))"]];
log.getRange("J4:J104").fillDown();

log.getRange("A9:K104").clear({ applyTo: "contents" });
log.getRange("H9:H104").formulas = log.getRange("H4:H99").formulas;
log.getRange("J9:J104").formulas = log.getRange("J4:J99").formulas;

log.getRange("A3:K3").format = {
  fill: "#D9EAF0",
  font: { bold: true },
  wrapText: true,
};
log.getRange("A3:K104").format = {
  borders: { insideHorizontal: { style: "Continuous", color: "#D9E2E7" }, insideVertical: { style: "Continuous", color: "#D9E2E7" } },
};
log.getRange("A4:D104").format.numberFormat = "0";
log.getRange("G4:G104").format.numberFormat = "0";
log.getRange("I4:I104").format.numberFormat = "0";
log.getRange("E4:F104").format.numberFormat = "yyyy-mm-dd hh:mm:ss.000";
log.freezePanes.freezeRows(3);

log.getRange("J4:J104").conditionalFormats.add("containsText", {
  text: "通过",
  format: { fill: "#D9EAD3", font: { color: "#274E13", bold: true } },
});
log.getRange("J4:J104").conditionalFormats.add("containsText", {
  text: "不通过",
  format: { fill: "#F4CCCC", font: { color: "#990000", bold: true } },
});
log.getRange("H4:H104").conditionalFormats.add("containsText", {
  text: "是",
  format: { fill: "#FCE5CD", font: { color: "#9C5700", bold: true } },
});

rules.getRange("A1:F1").merge();
rules.getRange("A1").values = [["判定规则和记录口径"]];
rules.getRange("A1").format = {
  fill: "#1F4E5F",
  font: { bold: true, color: "#FFFFFF", size: 14 },
};
rules.getRange("A3:B11").values = [
  ["指标", "说明"],
  ["目标同时运行数量", "本轮计划 run 起来的业务容器数量。"],
  ["成功启动数量", "docker run 成功返回并能查到容器的数量。"],
  ["最终running数量", "观察期结束后仍为 running 的容器数量。"],
  ["目标容器耗时(ms)", "你固定观察的那个容器从开始到运行结束的耗时。"],
  ["是否超过500ms", "由模板自动判断，超过阈值则为“是”。"],
  ["异常容器数", "exited、restarting、启动失败、业务异常的数量。"],
  ["判定通过", "成功启动数量=目标数量、最终running数量=目标数量、耗时未超过阈值、异常容器数=0。"],
  ["最大稳定数量", "最后一个判定为“通过”的目标同时运行数量。"],
];
rules.getRange("A3:B3").format = {
  fill: "#D9EAF0",
  font: { bold: true },
};
rules.getRange("A3:B11").format = {
  borders: { insideHorizontal: { style: "Continuous", color: "#D9E2E7" }, insideVertical: { style: "Continuous", color: "#D9E2E7" } },
  wrapText: true,
};

for (const sheet of [summary, log, rules]) {
  sheet.getUsedRange().format.autofitColumns();
  sheet.getUsedRange().format.autofitRows();
}

summary.getRange("A:A").format.columnWidthPx = 170;
summary.getRange("B:B").format.columnWidthPx = 360;
summary.getRange("D:H").format.columnWidthPx = 150;
log.getRange("A:K").format.columnWidthPx = 130;
log.getRange("K:K").format.columnWidthPx = 240;
rules.getRange("A:A").format.columnWidthPx = 180;
rules.getRange("B:B").format.columnWidthPx = 620;

const preview = await workbook.render({
  sheetName: "说明与结论",
  autoCrop: "all",
  scale: 1,
  format: "png",
});
await fs.writeFile(path.join(outputDir, "业务容器同时运行数量测试模板_预览.png"), new Uint8Array(await preview.arrayBuffer()));

const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 50 },
  summary: "formula error scan",
});
console.log(errors.ndjson);

const output = await SpreadsheetFile.exportXlsx(workbook);
const outputPath = path.join(outputDir, "业务容器同时运行数量测试模板.xlsx");
await output.save(outputPath);
console.log(outputPath);
