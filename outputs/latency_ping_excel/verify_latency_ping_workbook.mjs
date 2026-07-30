import { FileBlob, SpreadsheetFile } from "@oai/artifact-tool";

const inputPath = "E:/vs_projects/微服务容器编排/远程服务器连接_plus/outputs/latency_ping_excel/容器间最小通信延迟测试结果记录.xlsx";
const input = await FileBlob.load(inputPath);
const workbook = await SpreadsheetFile.importXlsx(input);

const table = await workbook.inspect({
  kind: "table",
  range: "测试记录!A1:K10",
  include: "values,formulas",
  tableMaxRows: 10,
  tableMaxCols: 11,
});
console.log(table.ndjson);

const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 100 },
  summary: "formula error scan",
});
console.log(errors.ndjson);
