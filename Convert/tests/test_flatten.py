# -*- coding: utf-8 -*-
"""nettest_flatten.py 的单元与 CLI 集成测试。

运行：python -m unittest discover -s tests -v
"""
import csv
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

import nettest_flatten as nf  # noqa: E402

FIXTURE = os.path.join(ROOT, "tests", "fixtures", "sample.csv")
REFERENCE_CSV = os.path.join(ROOT, "reference", "nettest-history(2).csv")

PING_GOOD = {
    "sent": 4, "received": 4, "lossPercent": 0, "rttMinMs": 18,
    "rttAverageMs": 18.75, "rttMaxMs": 19, "jitterMs": 0.3333333333333333,
    "samples": [
        {"sequence": 1, "status": "Success", "rttMs": 19},
        {"sequence": 2, "status": "Success", "rttMs": 19},
        {"sequence": 3, "status": "Success", "rttMs": 18},
        {"sequence": 4, "status": "Success", "rttMs": 19},
    ],
}

TRACERT = {
    "reachedTarget": True, "totalHops": 3,
    "hops": [
        {"index": 1, "address": "192.168.1.1", "attempts": [1, 1, None]},
        {"index": 2, "address": "10.0.0.1", "attempts": [2, None, None]},
        {"index": 3, "address": "93.184.216.34", "attempts": [3, 3, 2]},
    ],
}


def run_cli(args):
    """调用 main()，捕获 stdout/stderr，返回 (exit_code, stdout, stderr)。"""
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = nf.main(args)
    return code, out.getvalue(), err.getvalue()


class FlattenUnitTests(unittest.TestCase):
    def test_flatten_ping_wide(self):
        cols, values = nf.flatten(PING_GOOD)
        self.assertEqual(cols, [
            "metrics.sent", "metrics.received", "metrics.lossPercent",
            "metrics.rttMinMs", "metrics.rttAverageMs", "metrics.rttMaxMs",
            "metrics.jitterMs", "metrics.samples",
        ])
        self.assertEqual(values[:7], [4, 4, 0, 18, 18.75, 19, 0.3333333333333333])
        self.assertEqual(values[7], PING_GOOD["samples"])  # 数组保留原生结构

    def test_flatten_tracert_wide(self):
        cols, values = nf.flatten(TRACERT)
        self.assertEqual(cols[:2], ["metrics.reachedTarget", "metrics.totalHops"])
        self.assertEqual(values[:2], [True, 3])
        self.assertEqual(len(values[2]), 3)  # hops 数组保留

    def test_long_ping(self):
        cols, rows = nf.long_rows(PING_GOOD)
        self.assertEqual(len(rows), 4)
        self.assertEqual(cols[-3:], [
            "metrics.samples.sequence", "metrics.samples.status",
            "metrics.samples.rttMs",
        ])
        self.assertEqual(rows[0][-3:], [1, "Success", 19])
        self.assertEqual(rows[3][-3:], [4, "Success", 19])
        self.assertEqual(rows[0][0], 4)  # 父级标量 metrics.sent 冗余
        self.assertEqual(rows[2][-1], 18)  # 第 3 个样本 rttMs=18

    def test_long_tracert(self):
        cols, rows = nf.long_rows(TRACERT)
        self.assertEqual(len(rows), 3)
        self.assertEqual(cols[-3:], [
            "metrics.hops.index", "metrics.hops.address", "metrics.hops.attempts",
        ])
        self.assertEqual(rows[1][-3:], [2, "10.0.0.1", [2, None, None]])
        self.assertEqual(rows[2][-2], "93.184.216.34")
        # 父级标量冗余
        self.assertEqual(rows[0][0], True)  # metrics.reachedTarget
        self.assertEqual(rows[0][1], 3)     # metrics.totalHops

    def test_long_no_array_falls_back_to_wide(self):
        cols, rows = nf.long_rows({"sent": 4, "received": 4})
        self.assertEqual(cols, ["metrics.sent", "metrics.received"])
        self.assertEqual(rows, [[4, 4]])

    def test_long_empty_array_falls_back_to_wide(self):
        cols, rows = nf.long_rows({"samples": []})
        self.assertEqual(cols, ["metrics.samples"])
        self.assertEqual(rows, [[[]]])  # 单行，samples 列为空数组

    def test_metrics_rows_none(self):
        cols, rows = nf.metrics_rows(None, long_mode=False)
        self.assertEqual(cols, [])
        self.assertEqual(rows, [[]])
        cols, rows = nf.metrics_rows(None, long_mode=True)
        self.assertEqual(cols, [])
        self.assertEqual(rows, [[]])

    def test_find_split_path(self):
        self.assertEqual(nf.find_split_path({"samples": []}), None)
        self.assertEqual(
            nf.find_split_path({"a": {"b": [{"x": 1}]}})[0], ["a", "b"]
        )
        self.assertEqual(nf.find_split_path({"attempts": [1, 1, None]}), None)

    def test_json_text(self):
        self.assertEqual(nf._json_text(None), "")
        self.assertEqual(nf._json_text(True), "true")
        self.assertEqual(nf._json_text(False), "false")
        self.assertEqual(nf._json_text(0), "0")
        self.assertEqual(nf._json_text(18.75), "18.75")
        self.assertEqual(nf._json_text([1, None, "a"]), "[1,null,\"a\"]")

    def test_parse_metrics(self):
        self.assertIsNone(nf.parse_metrics(""))
        self.assertIsNone(nf.parse_metrics("null"))
        self.assertEqual(nf.parse_metrics('{"a": 1}'), {"a": 1})
        with self.assertRaises(json.JSONDecodeError):
            nf.parse_metrics("{oops")


class CliTests(unittest.TestCase):
    def test_wide_csv_stdout(self):
        code, out, err = run_cli([FIXTURE])
        self.assertEqual(code, 0)
        rows = list(csv.reader(io.StringIO(out)))
        self.assertEqual(len(rows), 6)  # 表头 + 5 数据行
        header = rows[0]
        self.assertIn("metrics.sent", header)
        self.assertIn("metrics.samples", header)
        first = dict(zip(header, rows[1]))
        self.assertEqual(first["runId"], "ping-good-0001")
        self.assertEqual(first["metrics.sent"], "4")
        self.assertEqual(first["metrics.received"], "4")
        self.assertEqual(first["metrics.lossPercent"], "0")
        self.assertEqual(first["metrics.rttAverageMs"], "18.75")
        self.assertEqual(first["metrics.jitterMs"], "0.3333333333333333")
        self.assertTrue(first["metrics.samples"].startswith("[{\"sequence\":1"))
        # 空 metrics 行保留，metrics 列留空
        empty = dict(zip(header, rows[3]))
        self.assertEqual(empty["runId"], "ping-empty-0003")
        self.assertEqual(empty["metrics.sent"], "")
        # 坏 JSON 行保留
        bad = dict(zip(header, rows[5]))
        self.assertEqual(bad["runId"], "ping-bad-0004")
        self.assertEqual(bad["metrics.sent"], "")
        # 坏 JSON 有警告
        self.assertIn("ping-bad-0004", err)
        self.assertIn("解析失败", err)

    def test_long_csv(self):
        code, out, _ = run_cli([FIXTURE, "--long"])
        self.assertEqual(code, 0)
        rows = list(csv.reader(io.StringIO(out)))
        # 4(ping-good) + 3(ping-loss) + 1(empty) + 3(tracert) + 1(bad) = 12
        self.assertEqual(len(rows), 13)
        header = rows[0]
        self.assertIn("metrics.samples.sequence", header)
        self.assertIn("metrics.samples.rttMs", header)
        self.assertIn("metrics.hops.index", header)
        self.assertIn("metrics.hops.address", header)
        self.assertIn("metrics.hops.attempts", header)
        # tracert 行的 attempts 保留为 JSON 文本
        tracert_rows = [r for r in rows[1:] if r[3] == "Tracert"]
        self.assertEqual(len(tracert_rows), 3)
        attempts_idx = header.index("metrics.hops.attempts")
        self.assertEqual(tracert_rows[0][attempts_idx], "[1,1,null]")
        self.assertEqual(tracert_rows[1][attempts_idx], "[2,null,null]")
        # 坏 JSON 行退化为单行
        bad_rows = [r for r in rows[1:] if r[0] == "ping-bad-0004"]
        self.assertEqual(len(bad_rows), 1)

    def test_jsonl(self):
        code, out, _ = run_cli([FIXTURE, "--format", "jsonl"])
        self.assertEqual(code, 0)
        lines = [json.loads(line) for line in out.splitlines() if line]
        self.assertEqual(len(lines), 5)
        self.assertEqual(lines[0]["metrics.sent"], 4)  # 原生 int
        self.assertEqual(lines[0]["metrics.samples"][0]["rttMs"], 19)
        self.assertEqual(lines[0]["metrics.lossPercent"], 0)  # 原生 int 0
        self.assertEqual(lines[1]["metrics.lossPercent"], 25.0)
        self.assertIsNone(lines[2]["metrics.sent"])  # 空 metrics 行 -> null
        self.assertEqual(lines[2]["runId"], "ping-empty-0003")
        self.assertEqual(lines[3]["metrics.reachedTarget"], True)

    def test_type_filter(self):
        code, out, _ = run_cli([FIXTURE, "--type", "tracert"])
        self.assertEqual(code, 0)
        rows = list(csv.reader(io.StringIO(out)))
        self.assertEqual(len(rows), 2)  # 表头 + 1
        self.assertEqual(rows[1][3], "Tracert")
        self.assertIn("metrics.hops", rows[0])
        self.assertNotIn("metrics.sent", rows[0])

    def test_strict_fails_on_bad_json(self):
        code, _, err = run_cli([FIXTURE, "--strict"])
        self.assertEqual(code, 1)
        self.assertIn("ping-bad-0004", err)

    def test_missing_metrics_column(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "bad.csv")
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write("a,b\n1,2\n")
            code, out, err = run_cli([path])
            self.assertEqual(code, 2)
            self.assertIn("metricsJson", err)

    def test_output_file_with_bom(self):
        with tempfile.TemporaryDirectory() as d:
            out_path = os.path.join(d, "out.csv")
            code, _, _ = run_cli([FIXTURE, "-o", out_path])
            self.assertEqual(code, 0)
            with open(out_path, "rb") as f:
                self.assertTrue(f.read(3) == b"\xef\xbb\xbf")

    def test_output_file_no_bom(self):
        with tempfile.TemporaryDirectory() as d:
            out_path = os.path.join(d, "out.csv")
            code, _, _ = run_cli([FIXTURE, "-o", out_path, "--no-bom"])
            self.assertEqual(code, 0)
            with open(out_path, "rb") as f:
                self.assertFalse(f.read(3) == b"\xef\xbb\xbf")


@unittest.skipUnless(os.path.exists(REFERENCE_CSV), "缺少 reference/nettest-history(2).csv")
class ReferenceCsvSmokeTests(unittest.TestCase):
    """用真实参考导出 CSV 做冒烟回归（数据行数动态计算，文件变化时自动调整）。"""

    def _run_cli(self, *args):
        p = subprocess.run(
            [sys.executable, os.path.join(ROOT, "nettest_flatten.py"), *args],
            capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(p.returncode, 0, p.stderr)
        return p.stdout

    @staticmethod
    def _count_rows():
        with open(REFERENCE_CSV, encoding="utf-8-sig", newline="") as f:
            return sum(1 for _ in csv.DictReader(f))

    def test_wide_row_count_and_first_row(self):
        n = self._count_rows()
        out = self._run_cli(REFERENCE_CSV)
        rows = list(csv.reader(io.StringIO(out)))
        self.assertEqual(len(rows), n + 1)
        hdr = rows[0]
        first = dict(zip(hdr, rows[1]))
        self.assertEqual(first["runId"], "07ab0384-9b80-4ee4-adec-a9abf23fecf9")
        self.assertEqual(first["metrics.sent"], "4")
        self.assertEqual(first["metrics.rttAverageMs"], "18.75")
        self.assertEqual(first["metrics.rttMinMs"], first["primaryLatencyMs"])
        samples = json.loads(first["metrics.samples"])
        self.assertEqual(len(samples), 4)
        self.assertNotIn("metrics.hops", hdr)  # 纯 Ping 数据

    def test_long_row_count(self):
        n = self._count_rows()
        out = self._run_cli(REFERENCE_CSV, "--long")
        rows = list(csv.reader(io.StringIO(out)))
        self.assertEqual(len(rows), n * 4 + 1)  # 全部 4 个 samples
        self.assertIn("metrics.samples.rttMs", rows[0])

    def test_jsonl_native_types(self):
        out = self._run_cli(REFERENCE_CSV, "--format", "jsonl")
        lines = [json.loads(line) for line in out.splitlines() if line]
        self.assertEqual(len(lines), self._count_rows())
        self.assertEqual(lines[0]["metrics.sent"], 4)
        self.assertIsInstance(lines[0]["metrics.sent"], int)
        self.assertEqual(lines[0]["metrics.samples"][3]["rttMs"], 18)


if __name__ == "__main__":
    unittest.main(verbosity=2)
