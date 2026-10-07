#!/usr/bin/env python3
"""
把 coverlet 生成的 cobertura 覆盖率报告转成一份可直接打开看的 HTML。

用法：
    python tests/coverage-report.py [输出路径]

默认读取 tests/logcat.Tests/TestResults/ 下最新的 coverage.cobertura.xml，
输出到 tests/coverage-report.html。
"""

import glob
import html
import os
import sys
import time
import xml.etree.ElementTree as ET

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SEARCH = os.path.join(REPO, "tests", "logcat.Tests", "TestResults")
DEFAULT_OUT = os.path.join(REPO, "tests", "coverage-report.html")


def find_report():
    pattern = os.path.join(SEARCH, "**", "coverage.cobertura.xml")
    files = glob.glob(pattern, recursive=True)
    if not files:
        sys.exit(f"没找到覆盖率报告，先跑：\n"
                 f"  dotnet test tests/logcat.Tests/logcat.Tests.csproj "
                 f"--collect:\"XPlat Code Coverage\" "
                 f"--settings tests/logcat.Tests/coverlet.runsettings\n"
                 f"（查找路径：{pattern}）")
    return max(files, key=os.path.getmtime)


def pct(rate):
    return float(rate) * 100.0


def color_for(rate):
    """覆盖率配色：>=95 绿，>=80 黄，其余红。"""
    p = pct(rate)
    if p >= 95:
        return "#1a7f37"
    if p >= 80:
        return "#9a6700"
    return "#cf222e"


def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    report = find_report()

    root = ET.parse(report).getroot()
    line_rate = float(root.get("line-rate", 0))
    branch_rate = float(root.get("branch-rate", 0))
    lines_covered = int(root.get("lines-covered", 0))
    lines_valid = int(root.get("lines-valid", 0))

    classes = []
    for cls in root.iter("class"):
        name = cls.get("name", "")
        filename = cls.get("filename", "")
        # 只看 logcat.* 的业务类型，过滤掉测试程序集
        if not name.startswith("logcat"):
            continue
        # coverlet 的 cobertura 输出会把同一行号的 <line> 条目重复输出两次，
        # 直接按类累加会把行数翻倍（百分比恰好不受影响），必须按行号去重
        line_hits: dict = {}
        for ln in cls.iter("line"):
            num = ln.get("number")
            hits = int(ln.get("hits", 0))
            line_hits[num] = max(line_hits.get(num, 0), hits)
        total = len(line_hits)
        if total == 0:
            continue
        covered = sum(1 for h in line_hits.values() if h > 0)
        classes.append({
            "name": name,
            "file": filename.replace("\\", "/"),
            "total": total,
            "covered": covered,
            "rate": covered / total,
        })

    classes.sort(key=lambda c: (c["rate"], c["name"]))
    total_lines = sum(c["total"] for c in classes)
    total_covered = sum(c["covered"] for c in classes)
    overall = total_covered / total_lines if total_lines else 0

    short = lambda n: n.split(".")[-1]
    rows = "\n".join(
        f"""      <tr>
        <td class="name">{html.escape(short(c['name']))}<span class="ns">{html.escape(c['name'][:-len(short(c['name'])) - 1])}</span></td>
        <td class="file">{html.escape(c['file'])}</td>
        <td class="num">{c['covered']:,} / {c['total']:,}</td>
        <td class="bar-cell">
          <div class="bar"><span style="width:{c['rate'] * 100:.1f}%;background:{color_for(c['rate'])}"></span></div>
        </td>
        <td class="pct" style="color:{color_for(c['rate'])}">{c['rate'] * 100:.1f}%</td>
      </tr>"""
        for c in classes
    )

    doc = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>logcat 单元测试覆盖率报告</title>
<style>
  :root {{
    --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --border: #d0d7de;
    --card: #f6f8fa;
  }}
  * {{ box-sizing: border-box; }}
  body {{
    margin: 0; padding: 40px 32px; background: var(--bg); color: var(--fg);
    font-family: "Segoe UI", "Microsoft YaHei", -apple-system, sans-serif;
    font-size: 14px; line-height: 1.6;
  }}
  .wrap {{ max-width: 1000px; margin: 0 auto; }}
  h1 {{ font-size: 24px; margin: 0 0 4px; }}
  .sub {{ color: var(--muted); font-size: 13px; margin-bottom: 28px; }}
  .hero {{
    display: flex; gap: 28px; align-items: center; padding: 24px 28px;
    border: 1px solid var(--border); border-radius: 10px; background: var(--card);
    margin-bottom: 28px; flex-wrap: wrap;
  }}
  .big {{ font-size: 56px; font-weight: 700; line-height: 1; }}
  .big span {{ font-size: 24px; font-weight: 500; }}
  .hero-meta {{ color: var(--muted); font-size: 13px; }}
  .hero-meta b {{ color: var(--fg); font-weight: 600; }}
  table {{ width: 100%; border-collapse: collapse; }}
  th {{ text-align: left; font-size: 12px; text-transform: uppercase; letter-spacing: .04em;
        color: var(--muted); padding: 0 12px 8px; border-bottom: 1px solid var(--border); }}
  td {{ padding: 9px 12px; border-bottom: 1px solid #eaeef2; vertical-align: middle; }}
  tr:hover td {{ background: #f6f8fa; }}
  .name {{ font-weight: 600; white-space: nowrap; }}
  .name .ns {{ display: block; font-weight: 400; font-size: 11px; color: var(--muted); }}
  .file {{ color: var(--muted); font-size: 12px; font-family: ui-monospace, Consolas, monospace; }}
  .num {{ font-variant-numeric: tabular-nums; color: var(--muted); white-space: nowrap; }}
  .bar-cell {{ width: 190px; }}
  .bar {{ height: 7px; background: #eaeef2; border-radius: 4px; overflow: hidden; }}
  .bar span {{ display: block; height: 100%; border-radius: 4px; }}
  .pct {{ font-weight: 700; font-variant-numeric: tabular-nums; text-align: right; width: 68px; }}
  .note {{ margin-top: 28px; padding: 16px 20px; border-left: 3px solid #d0d7de;
           background: var(--card); color: var(--muted); font-size: 13px; border-radius: 0 6px 6px 0; }}
  .note b {{ color: var(--fg); }}
  .note ul {{ margin: 8px 0 0; padding-left: 20px; }}
</style>
</head>
<body>
<div class="wrap">
  <h1>logcat 单元测试覆盖率报告</h1>
  <div class="sub">生成时间 {time.strftime('%Y-%m-%d %H:%M:%S')} · 数据源 {html.escape(os.path.relpath(report, REPO))}</div>

  <div class="hero">
    <div class="big" style="color:{color_for(overall)}">{overall * 100:.1f}<span>%</span></div>
    <div class="hero-meta">
      <div><b>行覆盖率</b>（统计口径内）</div>
      <div>{total_covered:,} / {total_lines:,} 行</div>
      <div>分支覆盖率 <b>{pct(branch_rate):.1f}%</b> · 原始行统计 {lines_covered:,} / {lines_valid:,}</div>
    </div>
  </div>

  <table>
    <thead>
      <tr><th>类型</th><th>源文件</th><th>已覆盖行</th><th>覆盖率</th><th></th></tr>
    </thead>
    <tbody>
{rows}
    </tbody>
  </table>

  <div class="note">
    <b>统计口径</b>：仅统计可单测的业务逻辑层（Services / Models）。
    <ul>
      <li>已排除 UI 层：<code>Forms</code> / <code>Controls</code> / <code>frmMain</code> / <code>Program</code> / <code>DpiFix</code> / <code>DpiDiag</code> / <code>Loc</code>——WinForms 代码依赖消息泵与 STA 线程；DPI 兜底、诊断打点与本地化的控件树遍历 / 字体测量都要真实窗口才有意义，单测成本高、收益低。</li>
      <li>已排除系统/设备集成层：<code>AdbManager</code>（需真实 adb 与设备）、<code>LogcatStream</code>（需设备流）、<code>ClipboardHelper</code>（Windows 剪贴板）、<code>StartupLog</code>（固定写用户目录）。</li>
      <li><code>LocTable.*</code>（纯数据译文表）<b>不</b>排除：它是本轮 i18n 的核心验证面，由 <code>LocTableTests</code> 的 18 条断言盯住（含扫源码查漏翻）。</li>
      <li>排除规则见 <code>tests/logcat.Tests/coverlet.runsettings</code>。</li>
    </ul>
  </div>
</div>
</body>
</html>
"""
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(doc)

    print(f"行覆盖率 {overall * 100:.2f}%  ({total_covered:,}/{total_lines:,})")
    print(f"报告已写入 {out_path}")


if __name__ == "__main__":
    main()
