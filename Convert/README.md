# nettest_flatten.py

独立 Python 命令行工具：把 NetTest 导出的历史 CSV（`GET /exports/history.csv` 的
产物，如 `nettest-history(2).csv`）中每行转义单列的 `metricsJson` 解析出来并
**展平**，输出可直接进 Excel / Pandas 分析的宽表或长表。

仅使用 Python 标准库，要求 Python >= 3.9，Windows / Linux / macOS 均可运行。

## 用法

```text
python nettest_flatten.py 输入.csv [选项]

选项：
  -o, --output FILE    输出文件；缺省输出到 stdout
  -f, --format FORMAT  输出格式：csv（默认）| jsonl
  -l, --long           把对象数组（samples/hops 等）拆为多行
  -t, --type TYPE      按 probeType 过滤，不区分大小写（Ping/Tracert/Dns/Https）
  --strict             metricsJson 解析失败时立即退出（默认跳过该列并报告）
  --no-bom             CSV 输出文件不带 UTF-8 BOM（默认带 BOM，Excel 兼容）
```

示例：

```text
python nettest_flatten.py "reference/nettest-history(2).csv" -o wide.csv
python nettest_flatten.py "reference/nettest-history(2).csv" --long -o long.csv
python nettest_flatten.py export.csv --format jsonl > export.jsonl
python nettest_flatten.py export.csv --type tracert --long -o hops.csv
```

## 输出列

- **原始列**：除 `metricsJson` 外的全部列原样保留（`runId`、`probeId`、
  `probeType`、`startedAtUtc`、`primaryLatencyMs` 等）。
- **metrics 标量**：递归展开为点号列，如 `metrics.sent`、`metrics.rttAverageMs`、
  `metrics.reachedTarget`、`metrics.totalHops`。
- **metrics 对象数组**（宽表模式）：保留为紧凑 JSON 字符串列，如
  `metrics.samples`、`metrics.hops`。
- **长表模式（--long）**：把 metrics 中第一个对象数组拆为多行——Ping 每样本
  一行（列：`metrics.samples.sequence/status/rttMs`），Tracert 每跳一行
  （列：`metrics.hops.index/address/attempts`）；父级标量冗余到每行。数组元素
  内部的标量数组（如 `attempts`）仍保留为 JSON 列。没有对象数组时退化为宽表。
- 列顺序 = 原始 CSV 头部顺序 + metrics 列首次出现顺序；多类型混合时取并集。

类型处理：JSON 数字按原样输出（`0` 不写成 `0.0`）；布尔按 JSON 字面量
（`true`/`false`）；JSONL 格式下保留原生 JSON 值（null 为 `null`），CSV 下
空值为空单元格。

## 行为与边界

- 输入读取 UTF-8（自动剥离 BOM）；CSV 输出默认带 BOM（`utf-8-sig`），
  Excel 直接打开不乱码；stdout 输出不带 BOM。
- `metricsJson` 为空或 `null`：该行仍输出，metrics 列留空。
- 解析失败：默认保留该行、metrics 列留空，stderr 报告输入行号与 runId；
  `--strict` 时立即以退出码 1 终止。退出码 2 表示输入文件不可用（如缺少
  `metricsJson` 列）。
- 两遍流式扫描，内存占用与列数相关、与行数无关，可处理大文件。
- 退出时向 stderr 报告统计：处理行数 / 输出行数 / 错误行数。

## 测试

```text
python -m unittest discover -s tests -v
```

- `tests/test_flatten.py`：展平规则单元测试 + CLI 集成测试（fixture）。
- `tests/fixtures/sample.csv`：Ping（含丢包）、Tracert、空 metrics、坏 JSON 样本。
- 真实数据冒烟：`reference/nettest-history(2).csv` 存在时自动追加运行
  `ReferenceCsvSmokeTests`（行数、首行指标与 `primaryLatencyMs` 交叉验证等）。

## 与 NetTest 规格的对应

- 输入格式对应 TechnicalSpecification 7.5 节导出 CSV（`metricsJson` 转义单列）。
- Ping/Tracert Metrics v1 结构见 TechnicalSpecification 6.2 / 6.3 节；
  通用递归展平同时覆盖 DNS（6.4）与 HTTPS（6.5）的 metrics，无需按类型维护 schema。
