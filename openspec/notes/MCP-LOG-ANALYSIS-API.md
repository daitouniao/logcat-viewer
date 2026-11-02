# MCP 日志分析接口定义（logcat）

- 状态：**待评审**（接口草案，与 `BACKLOG-MCP-AND-FORMATS.md` D5 的 v1 工具语义关系待拍板）
- 日期：2026-11-02 收录
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 与 BACKLOG 卡的关系

本笔记是 `BACKLOG-MCP-AND-FORMATS.md` D5（MCP v1 工具语义）之外的一套**分析型工具面**：工具从「AI 查日志的检索原语」（list / search / context / summarize / crashes）重定义为「日志分析统计原语」（probe / count / window / trace_field / cross_align）。两套口径的取舍、合并或并存方式未定，属本笔记的首要待决项。

## 通用约定

- 输入 `path`/`paths` 为 logcat 文本文件绝对路径；`mode`: `"substring" | "regex"`。
- 所有统计接口内置：流式全行扫描（返回 `scanned_lines`）、UTF-8/GBK 双编码自动判定、pid 正则 `\(\s*(\d+)\)`（扫行首 60 字符）、逐关键词独立计数、命中行全文不截断。
- 可选参数 `save_to`: 结果写入指定 txt 文件，返回该路径。

## 1. log_probe

```json
// 输入
{ "path": string }

// 输出
{
  "total_lines": number,
  "scanned_lines": number,
  "ts_range": { "first": string, "last": string },
  "encoding": { "utf8_cn_hits": number, "gbk_cn_hits": number, "chosen": "utf-8"|"gbk" },
  "line_styles": [ { "style": string, "count": number, "first_ts": string } ],
  "pid_dist": [ { "pid": number, "count": number, "proc": string|null } ]
}
```

## 2. log_count

```json
// 输入
{
  "paths": string[],
  "keywords": string[],
  "mode": "substring"|"regex",
  "by_pid": bool,          // 默认 true
  "by_second": bool,       // 默认 false
  "save_to": string|null
}

// 输出：每关键词一条，独立完整计数
{
  "results": [ {
    "keyword": string,
    "total": number,
    "pid_dist": { "<pid>": number },
    "per_second": [ [ts: string, n: number] ],
    "first_ts": string|null,
    "last_ts": string|null,
    "line_templates": string[]   // 去重后的命中行模板
  } ],
  "encoding_used": string,
  "scanned_lines": number
}
```

## 3. log_window

```json
// 输入
{
  "path": string,
  "start": string|null, "end": string|null,          // 与 around 二选一
  "around": { "keyword": string, "before_s": number, "after_s": number }|null,
  "filter": string|null,      // regex，窗口内二次过滤
  "pid": number|null,
  "context": number,          // 命中行前后上下文行数，默认 0
  "save_to": string|null
}

// 输出
{
  "hits": [ { "ts": string, "pid": number|null, "line": string,
              "before": string[], "after": string[] } ],
  "truncated": bool
}
```

## 4. log_trace_field

```json
// 输入
{
  "path": string,
  "pattern": string,          // regex，第 1 捕获组为数值/字符串值
  "group": number,            // 捕获组序号，默认 1
  "ts": bool,                 // 默认 true
  "delta": bool               // 相邻值差分，默认 false
}

// 输出
{
  "series": [ { "ts": string|null, "value": string|number, "delta": number|null } ],
  "count": number
}
```

## 5. log_cross_align

```json
// 输入
{
  "sources": [ { "path": string, "key": string, "mode": "substring"|"regex" } ],
  "tolerance_ms": number,     // 默认 500
  "save_to": string|null
}

// 输出
{
  "pairs": [ { "ts": string, "events": [ { "source": number, "line": string } ] } ],
  "unpaired": [ { "source": number, "ts": string, "line": string } ]
}
```
