#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""nettest_flatten.py

独立命令行工具：读取 NetTest 导出的历史 CSV（GET /exports/history.csv 的产物，
如 nettest-history(2).csv），把每行转义单列的 metricsJson 解析出来并展平：

- 宽表模式（默认）：嵌套对象递归展开为点号列（如 metrics.sent、metrics.hops.index），
  对象数组保留为紧凑 JSON 字符串列（如 metrics.samples）。
- 长表模式（--long）：把 metrics 中第一个对象数组（samples / hops / answers /
  redirects 等）拆为多行，父级标量冗余；元素内的标量数组（如 Tracert 的
  attempts）仍保留为 JSON 字符串列。

输出 CSV（默认，UTF-8 with BOM，兼容 Excel 直接打开）或 JSON Lines。
仅使用 Python 标准库，要求 Python >= 3.9。

示例：
    python nettest_flatten.py export.csv -o wide.csv
    python nettest_flatten.py export.csv --long -o long.csv
    python nettest_flatten.py export.csv --format jsonl > export.jsonl
    python nettest_flatten.py export.csv --type tracert
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import sys
from collections import OrderedDict
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

METRICS_COLUMN = "metricsJson"
SPLIT_PREFIX = "metrics"


class InputError(Exception):
    """输入文件层面错误（缺少 metricsJson 列等）。"""


class StrictError(Exception):
    """--strict 模式下遇 metricsJson 解析失败。"""


# ---------------------------------------------------------------- 输入

def iter_input_rows(path: str) -> Iterable[Tuple[int, Dict[str, str]]]:
    """流式读取输入 CSV，逐个产出 (数据行号, 行字典)。

    行号从 2 开始（第 1 行是表头），用于错误报告。读取编码 utf-8-sig，
    自动容忍或剥离 BOM。
    """
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        fieldnames = list(reader.fieldnames or [])
        if METRICS_COLUMN not in fieldnames:
            raise InputError(
                f"输入 CSV 缺少 '{METRICS_COLUMN}' 列；实际列: "
                + (", ".join(fieldnames) if fieldnames else "(空)")
            )
        for line_no, row in enumerate(reader, start=2):
            yield line_no, row


# ---------------------------------------------------------------- 展平

def _json_text(value: Any) -> str:
    """CSV 单元格文本：None -> 空；bool 按 JSON 字面量；数组/对象紧凑 JSON。"""
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def flatten(obj: Any, prefix: str = SPLIT_PREFIX) -> Tuple[List[str], List[Any]]:
    """宽表展平：返回 (列名, 原生值)。

    dict 递归展开为点号列；其余值（含 list）成为单列，list 保留 JSON 结构
    （CSV 输出时转为紧凑 JSON 文本，JSONL 输出时保持原生数组/对象）。
    """
    cols: List[str] = []
    values: List[Any] = []
    _flatten_into(obj, prefix, cols, values)
    return cols, values


def _flatten_into(obj: Any, prefix: str, cols: List[str], values: List[Any]) -> None:
    if isinstance(obj, dict):
        for key, value in obj.items():
            child = f"{prefix}.{key}" if prefix else key
            _flatten_into(value, child, cols, values)
    else:
        cols.append(prefix)
        values.append(obj)


def _is_split_list(value: Any) -> bool:
    """可作为拆行维度的对象数组：非空且元素全部为 dict。"""
    return (
        isinstance(value, list)
        and len(value) > 0
        and all(isinstance(item, dict) for item in value)
    )


def find_split_path(obj: Any) -> Optional[Tuple[List[str], List[Dict[str, Any]]]]:
    """深度优先查找 metrics 中第一个可拆行的对象数组，返回 (键路径, 数组)。

    只拆一级：数组元素内部的嵌套数组（如 Tracert 的 attempts）保留为 JSON 列。
    找不到（无数组或数组为空/元素非 dict）时返回 None。
    """
    if not isinstance(obj, dict):
        return None
    for key, value in obj.items():
        if _is_split_list(value):
            return [key], value
        found = find_split_path(value)
        if found is not None:
            path, items = found
            return [key] + path, items
    return None


def strip_path(obj: Any, path: Sequence[str]) -> Any:
    """返回去掉给定键路径后的浅拷贝，用于长表模式下提取父级标量。"""
    if not path or not isinstance(obj, dict):
        return obj
    key = path[0]
    if key not in obj:
        return obj
    if len(path) == 1:
        result = dict(obj)
        result.pop(key)
        return result
    result = dict(obj)
    result[key] = strip_path(obj[key], path[1:])
    return result


def long_rows(metrics: Dict[str, Any]) -> Tuple[List[str], List[List[Any]]]:
    """长表拆行：返回 (列名, 行值列表)。

    把 metrics 中第一个对象数组拆为多行，父级标量冗余到每行；数组元素内部
    的标量数组保留为 JSON 结构。没有可拆数组时退化为单行宽表。
    列名以首元素结构为基准，后续元素缺失的键补 None。
    """
    found = find_split_path(metrics)
    if found is None:
        cols, values = flatten(metrics)
        return cols, [values]
    path, items = found
    scalar_cols, scalar_values = flatten(strip_path(metrics, path))
    elem_prefix = ".".join([SPLIT_PREFIX] + path)
    elem_cols, _ = flatten(items[0], prefix=elem_prefix)
    rows: List[List[Any]] = []
    for item in items:
        ecols, evalues = flatten(item, prefix=elem_prefix)
        emap = dict(zip(ecols, evalues))
        rows.append(scalar_values + [emap.get(col) for col in elem_cols])
    return scalar_cols + elem_cols, rows


def parse_metrics(raw: Optional[str]) -> Optional[Any]:
    """解析 metricsJson 单元格；空或字面量 null 返回 None，坏 JSON 抛异常。"""
    text = (raw or "").strip()
    if text == "" or text == "null":
        return None
    return json.loads(text)


def metrics_rows(metrics: Any, long_mode: bool) -> Tuple[List[str], List[List[Any]]]:
    """把 metrics 转为 (列名, 行值列表)。

    metrics 为 None（空或解析失败）时返回单行、无 metrics 列——原 CSV 行仍输出，
    仅 metrics 列留空。
    """
    if metrics is None:
        return [], [[]]
    if long_mode:
        return long_rows(metrics)
    cols, values = flatten(metrics)
    return cols, [values]


# ---------------------------------------------------------------- 两遍扫描

def pass_scan(
    path: str, type_filter: Optional[str], long_mode: bool
) -> "OrderedDict[str, None]":
    """第一遍：收集输出列集合（按首现顺序），不保留数据。"""
    columns: "OrderedDict[str, None]" = OrderedDict()
    for _line_no, row in iter_input_rows(path):
        if type_filter is not None and row.get("probeType", "").lower() != type_filter:
            continue
        # 原始列（除 metricsJson）按 CSV 头部顺序先行加入
        for col in row:
            if col != METRICS_COLUMN:
                columns.setdefault(col, None)
        try:
            metrics = parse_metrics(row.get(METRICS_COLUMN))
        except json.JSONDecodeError:
            continue  # 坏 JSON 不贡献 metrics 列
        cols, _ = metrics_rows(metrics, long_mode)
        for col in cols:
            columns.setdefault(col, None)
    return columns


def pass_write(
    path: str,
    type_filter: Optional[str],
    long_mode: bool,
    columns: Sequence[str],
    stats: Dict[str, int],
    out: io.TextIOBase,
    fmt: str,
    strict: bool,
) -> None:
    """第二遍：逐行展平并写出。"""
    if fmt == "csv":
        writer = csv.writer(out, lineterminator="\n")
        writer.writerow(list(columns))

    for line_no, row in iter_input_rows(path):
        if type_filter is not None and row.get("probeType", "").lower() != type_filter:
            continue
        stats["processed"] += 1
        try:
            metrics = parse_metrics(row.get(METRICS_COLUMN))
        except json.JSONDecodeError as exc:
            stats["errors"] += 1
            run_id = row.get("runId", "")
            print(
                f"警告: 第 {line_no} 行 (runId={run_id}) '{METRICS_COLUMN}' 解析失败: {exc}",
                file=sys.stderr,
            )
            if strict:
                raise StrictError(
                    f"第 {line_no} 行 (runId={run_id}) '{METRICS_COLUMN}' 解析失败: {exc}"
                ) from exc
            metrics = None

        cols, rows = metrics_rows(metrics, long_mode)
        for values in rows:
            value_map = dict(zip(cols, values))
            out_row = [row.get(c) if c in row else value_map.get(c) for c in columns]
            if fmt == "csv":
                writer.writerow([_json_text(v) for v in out_row])
            else:
                obj = OrderedDict((c, v) for c, v in zip(columns, out_row))
                out.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")))
                out.write("\n")
            stats["written"] += 1


# ---------------------------------------------------------------- CLI

def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="nettest_flatten.py",
        description="展平 NetTest 导出 CSV 中的 metricsJson 为宽表或长表。",
        epilog="示例: python nettest_flatten.py export.csv --long -o long.csv",
    )
    parser.add_argument("input", help="NetTest 导出的历史 CSV 路径")
    parser.add_argument(
        "-o", "--output", metavar="FILE", help="输出文件；缺省输出到 stdout"
    )
    parser.add_argument(
        "-f", "--format", choices=("csv", "jsonl"), default="csv",
        help="输出格式（默认 csv）",
    )
    parser.add_argument(
        "-l", "--long", action="store_true",
        help="把对象数组（samples/hops 等）拆为多行",
    )
    parser.add_argument(
        "-t", "--type", metavar="TYPE",
        help="按 probeType 过滤，不区分大小写（Ping/Tracert/Dns/Https）",
    )
    parser.add_argument(
        "--strict", action="store_true",
        help="metricsJson 解析失败时立即退出（默认跳过该列并报告）",
    )
    parser.add_argument(
        "--no-bom", action="store_true",
        help="CSV 输出文件不带 UTF-8 BOM（仅影响 --output 文件）",
    )
    args = parser.parse_args(argv)

    # 统一 stderr 为 UTF-8：Windows 下重定向到管道/文件时 Python 默认按
    # locale 编码（如 GBK）输出，会导致 UTF-8 消费者解码失败。
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, io.UnsupportedOperation):
        pass

    type_filter = args.type.lower() if args.type else None

    try:
        columns = pass_scan(args.input, type_filter, args.long)
    except InputError as exc:
        print(f"错误: {exc}", file=sys.stderr)
        return 2

    if args.output:
        encoding = "utf-8"
        if args.format == "csv" and not args.no_bom:
            encoding = "utf-8-sig"
        out = open(args.output, "w", encoding=encoding, newline="")
        close_out = True
    else:
        out = sys.stdout
        close_out = False
        try:
            sys.stdout.reconfigure(encoding="utf-8", newline="")
        except (AttributeError, io.UnsupportedOperation):
            pass

    stats = {"processed": 0, "written": 0, "errors": 0}
    try:
        pass_write(
            args.input, type_filter, args.long, list(columns), stats, out,
            args.format, strict=args.strict,
        )
    except StrictError as exc:
        print(f"错误: {exc}", file=sys.stderr)
        return 1
    except InputError as exc:
        print(f"错误: {exc}", file=sys.stderr)
        return 2
    finally:
        if close_out:
            out.close()

    print(
        f"完成: 处理 {stats['processed']} 行，输出 {stats['written']} 行，"
        f"metricsJson 错误 {stats['errors']} 行。",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
