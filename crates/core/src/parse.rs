//! logcat 行解析与索引增量（与 C# `LogParser` / `LogDocument` 的索引口径对齐）。
//!
//! 行口径：空行与横幅行不进索引，其余物理行各成一条记录；无法识别的行保留为
//! 「元数据未知」记录（pid/tid = -1、level = 0、分钟 = 255、无 tag），不丢行。
//! 崩溃打标与段化装配在 [`crate::segment`]，本模块只负责行解析。

use std::sync::Arc;

use crate::segment::Segment;
use crate::sidecar::Payload;

pub const MINUTE_NONE: u8 = 255;

const LVL_UNKNOWN: u8 = 0;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LineMeta {
    pub minute: u8,
    pub pid: i32,
    pub tid: i32,
    pub level: u8,
    pub tag: Option<String>,
    pub msg_offset: usize,
}

impl LineMeta {
    fn unknown() -> Self {
        Self {
            minute: MINUTE_NONE,
            pid: -1,
            tid: -1,
            level: LVL_UNKNOWN,
            tag: None,
            msg_offset: 0,
        }
    }
}

fn level_code(b: u8) -> u8 {
    match b {
        b'V' => 1,
        b'D' => 2,
        b'I' => 3,
        b'W' => 4,
        b'E' => 5,
        b'F' => 6,
        b'A' => 7,
        _ => 0,
    }
}

fn parse_uint(bytes: &[u8]) -> Option<i32> {
    if bytes.is_empty() {
        return None;
    }
    let mut v: i32 = 0;
    for b in bytes {
        if !b.is_ascii_digit() {
            return None;
        }
        v = v.checked_mul(10)?.checked_add((b - b'0') as i32)?;
    }
    Some(v)
}

/// 括号内 pid 允许右对齐填充空格（`( 1798)`，C# `TryParseInt` 同口径）。
fn parse_uint_trim(bytes: &[u8]) -> Option<i32> {
    let mut i = 0;
    while i < bytes.len() && bytes[i] == b' ' {
        i += 1;
    }
    parse_uint(&bytes[i..])
}

fn digits_until(line: &[u8], mut i: usize) -> usize {
    while i < line.len() && line[i].is_ascii_digit() {
        i += 1;
    }
    i
}

fn timestamp_fields_valid(month: i32, day: i32, hour: i32, minute: i32, second: i32) -> bool {
    (1..=12).contains(&month)
        && (1..=31).contains(&day)
        && (0..=23).contains(&hour)
        && (0..=59).contains(&minute)
        && (0..=60).contains(&second)
}

fn two_digits(line: &[u8], i: usize) -> Option<(u32, usize)> {
    let a = *line.get(i)?;
    let b = *line.get(i + 1)?;
    if !a.is_ascii_digit() || !b.is_ascii_digit() {
        return None;
    }
    Some((((a - b'0') as u32) * 10 + (b - b'0') as u32, i + 2))
}

/// 方括号 long 格式：`[ [yyyy-]mm-dd HH:MM:SS[.,]fraction pid: tid L/Tag ] msg`，
/// 语法与 C# `ReLong` 对齐：pid 与 tid 均可缺、`]` 后允许无空格、tag 可含空格。
fn parse_long(line: &[u8]) -> Option<LineMeta> {
    let n = line.len();
    let mut i = 1;
    while i < n && line[i].is_ascii_whitespace() {
        i += 1;
    }

    // 可选年
    if i + 4 < n
        && line[i..i + 4].iter().all(u8::is_ascii_digit)
        && line[i + 4] == b'-'
    {
        i += 5;
    }

    let (month, p) = two_digits(line, i)?;
    if *line.get(p)? != b'-' {
        return None;
    }
    let (day, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b' ' {
        return None;
    }
    let (hour, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b':' {
        return None;
    }
    let (minute, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b':' {
        return None;
    }
    let (second, p) = two_digits(line, p + 1)?;
    if !timestamp_fields_valid(
        month as i32,
        day as i32,
        hour as i32,
        minute as i32,
        second as i32,
    ) {
        return None;
    }
    let sep = *line.get(p)?;
    if sep != b'.' && sep != b',' {
        return None;
    }
    let frac_end = digits_until(line, p + 1);
    if frac_end == p + 1 || frac_end - (p + 1) > 9 {
        return None;
    }
    let mut j = frac_end;
    if !line.get(j)?.is_ascii_whitespace() {
        return None;
    }
    while j < n && line[j].is_ascii_whitespace() {
        j += 1;
    }

    let mut pid = -1;
    let k = digits_until(line, j);
    if k > j && line.get(k) == Some(&b':') {
        pid = parse_uint(&line[j..k]).unwrap_or(-1);
        j = k + 1;
        while j < n && line[j].is_ascii_whitespace() {
            j += 1;
        }
    }
    let mut tid = -1;
    let k = digits_until(line, j);
    if k > j {
        tid = parse_uint(&line[j..k]).unwrap_or(-1);
        j = k;
    }
    while j < n && line[j].is_ascii_whitespace() {
        j += 1;
    }

    let level = line.get(j).map(|b| level_code(*b)).unwrap_or(0);
    if level == 0 || line.get(j + 1) != Some(&b'/') {
        return None;
    }
    j += 2;
    let close = j + line[j..].iter().position(|b| *b == b']')?;
    let mut tag_end = close;
    while tag_end > j && line[tag_end - 1].is_ascii_whitespace() {
        tag_end -= 1;
    }
    let tag = utf8_trim(&line[j..tag_end]);
    let mut msg_offset = close + 1;
    if msg_offset < n && line[msg_offset].is_ascii_whitespace() {
        msg_offset += 1;
    }

    Some(LineMeta {
        minute: minute as u8,
        pid,
        tid,
        level,
        tag,
        msg_offset,
    })
}

/// 横幅 / ROM 自报头：至少 5 个连续 `-`，其后为空白或行尾。
pub fn is_banner(line: &[u8]) -> bool {
    let mut d = 0;
    while d < line.len() && line[d] == b'-' {
        d += 1;
    }
    if d < 5 {
        return false;
    }
    d >= line.len() || line[d] == b' ' || line[d] == b'\t'
}

/// 按行首形态解析元数据。时间戳非法时只丢时间，不影响其余字段。
pub fn parse_line(line: &[u8]) -> LineMeta {
    let n = line.len();
    if n == 0 {
        return LineMeta::unknown();
    }
    if line[0] == b'[' {
        if let Some(meta) = parse_long(line) {
            return meta;
        }
        return LineMeta::unknown();
    }

    let mut meta = LineMeta::unknown();
    let mut p = 0usize;

    if n >= 16
        && line[2] == b'-'
        && line[5] == b' '
        && line[8] == b':'
        && line[11] == b':'
        && line[14] == b'.'
    {
        let f = digits_until(line, 15);
        if f > 15 {
            let (Some(month), Some(day), Some(hour), Some(minute), Some(second)) = (
                parse_uint(&line[0..2]),
                parse_uint(&line[3..5]),
                parse_uint(&line[6..8]),
                parse_uint(&line[9..11]),
                parse_uint(&line[12..14]),
            ) else {
                return LineMeta::unknown();
            };
            if !timestamp_fields_valid(month, day, hour, minute, second) {
                return LineMeta::unknown();
            }
            meta.minute = minute as u8;
            p = f;
        }
    } else if n >= 21
        && line[4] == b'-'
        && line[7] == b'-'
        && line[10] == b' '
        && line[13] == b':'
        && line[16] == b':'
        && line[19] == b'.'
    {
        let f = digits_until(line, 20);
        if f > 20 {
            let (Some(month), Some(day), Some(hour), Some(minute), Some(second)) = (
                parse_uint(&line[5..7]),
                parse_uint(&line[8..10]),
                parse_uint(&line[11..13]),
                parse_uint(&line[14..16]),
                parse_uint(&line[17..19]),
            ) else {
                return LineMeta::unknown();
            };
            if !timestamp_fields_valid(month, day, hour, minute, second) {
                return LineMeta::unknown();
            }
            meta.minute = minute as u8;
            p = f;
        }
    }

    while p < n && line[p] == b' ' {
        p += 1;
    }

    if p < n {
        // threadtime：pid tid L Tag: msg（tag 也可能带 (pid) 后缀）
        let q = digits_until(line, p);
        if q > p && q < n && line[q] == b' ' {
            let mut r = q;
            while r < n && line[r] == b' ' {
                r += 1;
            }
            let s = digits_until(line, r);
            if s > r && s < n && (line[s] == b' ' || line[s] == b'/') {
                let mut t = s;
                while t < n && line[t] == b' ' {
                    t += 1;
                }
                let lvl = if t < n { level_code(line[t]) } else { 0 };
                if lvl != 0 {
                    meta.level = lvl;
                    meta.pid = parse_uint(&line[p..q]).unwrap_or(-1);
                    meta.tid = parse_uint(&line[r..s]).unwrap_or(-1);
                    if t + 1 < n && line[t + 1] == b' ' {
                        let (tag, mo) = split_tag_msg(line, t + 2);
                        meta.tag = tag;
                        meta.msg_offset = mo;
                        return meta;
                    }
                    if t + 1 < n && line[t + 1] == b'/' {
                        if let Some((tag, pid, mo)) = parse_slash_form(line, t + 2) {
                            meta.tag = tag;
                            meta.pid = pid.unwrap_or(meta.pid);
                            meta.msg_offset = mo;
                            return meta;
                        }
                    }
                    return meta;
                }
            }
        }

        // time / brief / tag：L/Tag(pid): msg
        let lvl = level_code(line[p]);
        if lvl != 0 && p + 1 < n && line[p + 1] == b'/' {
            if let Some((tag, pid, mo)) = parse_slash_form(line, p + 2) {
                meta.level = lvl;
                meta.tag = tag;
                meta.pid = pid.unwrap_or(-1);
                meta.tid = -1;
                meta.msg_offset = mo;
            }
        }
    }

    meta
}

/// 行首时间戳区间 `(start, len)`（字节偏移）：只由行首语法决定，与 pid/tid/级别/tag
/// 是否识别无关。无年 / 带年 threadtime 取到小数秒末位；方括号 long 跳过 `[ ` 前缀后
/// 同样止于小数秒末位（无小数秒时止于秒末位，`.` 与 `,` 均可作小数分隔）。
/// 行首不是可识别时间戳时返回 `(0, 0)`。
pub fn time_range(line: &[u8]) -> (u32, u32) {
    let Some((start, end)) = timestamp_span(line) else {
        return (0, 0);
    };
    (start as u32, (end - start) as u32)
}

fn timestamp_span(line: &[u8]) -> Option<(usize, usize)> {
    let n = line.len();
    let mut i = match line.first() {
        Some(b'[') => {
            let mut j = 1;
            while j < n && line[j].is_ascii_whitespace() {
                j += 1;
            }
            j
        }
        _ => 0,
    };
    let start = i;
    if i + 4 < n && line[i..i + 4].iter().all(u8::is_ascii_digit) && line[i + 4] == b'-' {
        i += 5;
    }
    let (month, p) = two_digits(line, i)?;
    if *line.get(p)? != b'-' {
        return None;
    }
    let (day, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b' ' {
        return None;
    }
    let (hour, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b':' {
        return None;
    }
    let (minute, p) = two_digits(line, p + 1)?;
    if *line.get(p)? != b':' {
        return None;
    }
    let (second, p) = two_digits(line, p + 1)?;
    if !timestamp_fields_valid(
        month as i32,
        day as i32,
        hour as i32,
        minute as i32,
        second as i32,
    ) {
        return None;
    }
    let end = match line.get(p) {
        Some(b'.') | Some(b',') => {
            let f = digits_until(line, p + 1);
            if f == p + 1 || f - (p + 1) > 9 {
                return None;
            }
            if matches!(line.get(f), Some(b) if !b.is_ascii_whitespace()) {
                return None;
            }
            f
        }
        Some(b) if b.is_ascii_whitespace() => p,
        None => p,
        _ => return None,
    };
    Some((start, end))
}

/// `Tag(pid): msg` / `Tag: msg` / `Tag` → (tag, pid, msg 起始偏移)。
fn parse_slash_form(line: &[u8], start: usize) -> Option<(Option<String>, Option<i32>, usize)> {
    let n = line.len();
    if start > n {
        return None;
    }
    let rest = &line[start..];
    let paren = rest.iter().position(|b| *b == b'(');
    let colon = rest.iter().position(|b| *b == b':');
    if let Some(b) = paren {
        if colon.map(|c| b < c).unwrap_or(true) {
            let tag = utf8_trim(&rest[..b]);
            let e = rest[b..].iter().position(|x| *x == b')')? + b;
            let pid = parse_uint_trim(&rest[b + 1..e]);
            let mut mo = start + e + 1;
            if mo < n && line[mo] == b':' {
                mo += 1;
                if mo < n && line[mo] == b' ' {
                    mo += 1;
                }
            }
            return Some((tag, pid, mo));
        }
    }
    if let Some(c) = colon {
        let tag = utf8_trim(&rest[..c]);
        let mut mo = start + c + 1;
        if mo < n && line[mo] == b' ' {
            mo += 1;
        }
        return Some((tag, None, mo));
    }
    let sp = rest.iter().position(|b| *b == b' ');
    match sp {
        None => Some((utf8_trim(rest), None, n)),
        Some(i) => Some((utf8_trim(&rest[..i]), None, start + i + 1)),
    }
}

/// `Tag: msg`：tag 去掉右对齐填充空格，msg 跳过冒号后的一个空格。
fn split_tag_msg(line: &[u8], start: usize) -> (Option<String>, usize) {
    if start > line.len() {
        return (None, line.len());
    }
    let rest = &line[start..];
    if let Some(c) = rest.iter().position(|b| *b == b':') {
        let mut end = c;
        while end > 0 && rest[end - 1] == b' ' {
            end -= 1;
        }
        let mut mo = start + c + 1;
        if mo < line.len() && line[mo] == b' ' {
            mo += 1;
        }
        return (utf8_trim(&rest[..end]), mo);
    }
    // 没有冒号：tag 到第一个空格为止
    match rest.iter().position(|b| *b == b' ') {
        Some(i) => (utf8_trim(&rest[..i]), start + i + 1),
        None => (utf8_trim(rest), line.len()),
    }
}

fn utf8_trim(bytes: &[u8]) -> Option<String> {
    let s = String::from_utf8_lossy(bytes);
    let t = s.trim();
    if t.is_empty() {
        None
    } else {
        Some(t.to_string())
    }
}

/// 单段索引器：对 [`crate::segment::Store`] 的兼容壳（整份解析与流式行计数共用）。
#[derive(Default)]
pub struct Indexer {
    store: crate::segment::Store,
}

impl Indexer {
    pub fn new() -> Self {
        Self::default()
    }

    /// 追加一行；空行 / 横幅返回 false（不成记录）。`offset` 为该行在文档中的起始字节。
    pub fn add_line(&mut self, line: &[u8], offset: u64) -> bool {
        self.store.push_line(line, offset)
    }

    pub fn row_count(&self) -> u64 {
        self.store.rows() as u64
    }

    /// 冻结当前段；转发 [`crate::segment::Store::freeze`]。
    pub fn freeze(&mut self) {
        self.store.freeze();
    }

    /// 已冻结的段（行序）；转发 [`crate::segment::Store::frozen`]。
    pub fn frozen(&self) -> &[Arc<Segment>] {
        self.store.frozen()
    }

    pub fn into_payload(self) -> Payload {
        self.store.into_payload()
    }
}

/// 解析整份文件字节：行偏移相对文件起点；首行 UTF-8 BOM 剥掉不参与解析（偏移 +3）。
pub fn parse_bytes(data: &[u8]) -> Payload {
    let mut indexer = Indexer::new();
    let mut pos = 0usize;
    let mut first = true;
    while pos < data.len() {
        let nl = data[pos..].iter().position(|b| *b == b'\n').map(|i| pos + i);
        let line_end = nl.unwrap_or(data.len());
        let mut raw = &data[pos..line_end];
        if raw.last() == Some(&b'\r') {
            raw = &raw[..raw.len() - 1];
        }
        let mut line_off = pos;
        let mut line = raw;
        if first && line.starts_with(&[0xEF, 0xBB, 0xBF]) {
            line_off += 3;
            line = &line[3..];
        }
        first = false;
        indexer.add_line(line, line_off as u64);
        pos = match nl {
            Some(n) => n + 1,
            None => data.len(),
        };
    }
    indexer.into_payload()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_threadtime_columns() {
        let line = b"11-14 12:00:01.123  1234  5678 I MyTag: hello world";
        let m = parse_line(line);
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 1234);
        assert_eq!(m.tid, 5678);
        assert_eq!(m.level, 3);
        assert_eq!(m.tag.as_deref(), Some("MyTag"));
        assert_eq!(&line[m.msg_offset..], b"hello world");
    }

    #[test]
    fn parse_year_form_and_right_aligned_tag() {
        let line = b"2026-11-14 12:00:01.123  42  43 E netd    : boom";
        let m = parse_line(line);
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 42);
        assert_eq!(m.tid, 43);
        assert_eq!(m.level, 5);
        assert_eq!(m.tag.as_deref(), Some("netd"));
        assert_eq!(&line[m.msg_offset..], b"boom");
    }

    #[test]
    fn parse_brief_and_unknown_lines() {
        let m = parse_line(b"I/ActivityManager( 1798): started");
        assert_eq!(m.level, 3);
        assert_eq!(m.pid, 1798);
        assert_eq!(m.tag.as_deref(), Some("ActivityManager"));
        assert_eq!(m.minute, MINUTE_NONE);

        let m = parse_line(b"    continuation line");
        assert_eq!(m, LineMeta::unknown());
    }

    #[test]
    fn parse_long_format_variants() {
        // 可选年 + pid: tid
        let line = b"[ 2026-11-14 12:00:01.123  1234: 5678 I/MyTag ] hello world";
        let m = parse_line(line);
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 1234);
        assert_eq!(m.tid, 5678);
        assert_eq!(m.level, 3);
        assert_eq!(m.tag.as_deref(), Some("MyTag"));
        assert_eq!(&line[m.msg_offset..], b"hello world");

        // 逗号小数 + 分钟透出
        let line = b"[ 11-14 12:34:56,5  42: 43 D/netd ] boom";
        let m = parse_line(line);
        assert_eq!(m.minute, 34);
        assert_eq!(m.pid, 42);
        assert_eq!(m.tid, 43);
        assert_eq!(m.level, 2);
        assert_eq!(m.tag.as_deref(), Some("netd"));
        assert_eq!(&line[m.msg_offset..], b"boom");

        // 无 tid：tid 记 -1
        let line = b"[ 11-14 12:00:01.123  1234: I/Tag ] msg";
        let m = parse_line(line);
        assert_eq!(m.pid, 1234);
        assert_eq!(m.tid, -1);
        assert_eq!(m.level, 3);
        assert_eq!(m.tag.as_deref(), Some("Tag"));
        assert_eq!(&line[m.msg_offset..], b"msg");

        // `]` 后无空格：消息偏移紧贴 `]`
        let line = b"[ 11-14 12:00:01.123  1: 2 W/X ]tight";
        let m = parse_line(line);
        assert_eq!(m.level, 4);
        assert_eq!(m.tag.as_deref(), Some("X"));
        assert_eq!(&line[m.msg_offset..], b"tight");
    }

    #[test]
    fn parse_long_format_variants_after_bracket_no_space() {
        // `[` 后无空格
        let line = b"[11-14 12:00:01.123  7: 8 E/T ] msg";
        let m = parse_line(line);
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 7);
        assert_eq!(m.tid, 8);
        assert_eq!(m.level, 5);
        assert_eq!(m.tag.as_deref(), Some("T"));
        assert_eq!(&line[m.msg_offset..], b"msg");
    }

    #[test]
    fn timestamp_field_range_rejects() {
        let out_of_range: &[&[u8]] = &[
            b"13-14 12:00:01.123  1  2 I T: month",
            b"11-00 12:00:01.123  1  2 I T: day",
            b"11-14 24:00:01.123  1  2 I T: hour",
            b"11-14 12:60:01.123  1  2 I T: minute",
            b"11-14 12:00:61.123  1  2 I T: second",
            b"2026-13-14 12:00:01.123  1  2 I T: yyyy-month",
            b"[ 11-14 24:00:01.123  1: 2 I/T ] long-hour",
            b"[ 00-14 12:00:01.123  1: 2 I/T ] long-month",
        ];
        for line in out_of_range {
            assert_eq!(
                parse_line(line),
                LineMeta::unknown(),
                "越界行应整行未知：{}",
                String::from_utf8_lossy(line)
            );
        }

        // 行不丢：越界行仍成一条未知元数据记录
        let payload = parse_bytes(b"13-14 12:00:01.123  1  2 I T: bad\n");
        assert_eq!(payload.row_count(), 1);
        assert_eq!(payload.minutes, vec![MINUTE_NONE]);
        assert_eq!(payload.pids, vec![-1]);
        assert_eq!(payload.tag_ids, vec![-1]);
    }

    #[test]
    fn timestamp_range_valid_calendar_invalid_keep() {
        // 02-31：范围合法但日历非法，保持解析
        let m = parse_line(b"02-31 12:00:01.123  1  2 I T: x");
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 1);
        assert_eq!(m.tid, 2);
        assert_eq!(m.level, 3);
        assert_eq!(m.tag.as_deref(), Some("T"));

        // 秒 60（闰秒）保持解析，分钟按原始值归属
        let m = parse_line(b"11-14 12:34:60.123  1  2 I T: x");
        assert_eq!(m.minute, 34);
        assert_eq!(m.pid, 1);

        // 年 0000 不设下界
        let m = parse_line(b"0000-01-01 00:00:00.000  1  2 I T: x");
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 1);

        // long 形态同样接受闰秒
        let m = parse_line(b"[ 11-14 12:00:60.123  1: 2 I/T ] x");
        assert_eq!(m.minute, 0);
        assert_eq!(m.pid, 1);
        assert_eq!(m.tid, 2);
        assert_eq!(m.level, 3);
        assert_eq!(m.tag.as_deref(), Some("T"));
    }

    #[test]
    fn crash_binds_to_parent_row() {
        let data = b"11-14 12:00:01.000  1  2 I X: hello\n\
11-14 12:00:02.000  100  200 E AndroidRuntime: FATAL EXCEPTION: main\n\
11-14 12:00:02.001  100  200 E AndroidRuntime: Process: com.example.app, PID: 100\n\
11-14 12:00:02.002  100  200 E AndroidRuntime: java.lang.IllegalStateException: boom\n";
        let payload = parse_bytes(data);
        assert_eq!(payload.row_count(), 4);
        assert_eq!(payload.crashes.len(), 1);
        let c = &payload.crashes[0];
        assert_eq!(c.row, 1);
        assert_eq!(c.kind, crate::crash::KIND_JAVA);
        assert_eq!(c.pid, 100);
        assert_eq!(c.tid, 200);
        assert_eq!(c.exc_id, 0);
        assert_eq!(c.pkg_id, 0);
        assert_eq!(
            payload.exc_types,
            vec!["java.lang.IllegalStateException".to_string()]
        );
        assert_eq!(payload.pkgs, vec!["com.example.app".to_string()]);
    }

    #[test]
    fn crash_extract_independent_of_timestamp() {
        // 崩溃头行时间戳月越界 → 整行元数据未知，但崩溃打标仍成立（绑定该行、pid/tid 记 -1）
        let data = b"13-14 12:00:02.000  100  200 E AndroidRuntime: FATAL EXCEPTION: main\n\
11-14 12:00:02.001  100  200 E AndroidRuntime: Process: com.example.app, PID: 100\n\
11-14 12:00:02.002  100  200 E AndroidRuntime: java.lang.IllegalStateException: boom\n";
        let payload = parse_bytes(data);
        assert_eq!(payload.row_count(), 3);
        assert_eq!(payload.minutes[0], MINUTE_NONE);
        assert_eq!(payload.pids[0], -1);
        assert_eq!(payload.crashes.len(), 1);
        let c = &payload.crashes[0];
        assert_eq!(c.row, 0);
        assert_eq!(c.kind, crate::crash::KIND_JAVA);
        assert_eq!(c.pid, -1);
        assert_eq!(c.tid, -1);
        assert_eq!(
            payload.exc_types,
            vec!["java.lang.IllegalStateException".to_string()]
        );
        assert_eq!(payload.pkgs, vec!["com.example.app".to_string()]);
    }

    #[test]
    fn index_skips_empty_and_banner_keeps_unknown() {
        let data = b"--------- beginning of main\n11-14 12:00:01.123  1  2 I T: a\n\n   cont\r\n";
        let payload = parse_bytes(data);
        assert_eq!(payload.row_count(), 2);
        assert_eq!(payload.minutes, vec![0, MINUTE_NONE]);
        assert_eq!(payload.pids, vec![1, -1]);
        assert_eq!(payload.tag_ids, vec![0, -1]);
        assert_eq!(payload.tags, vec!["T".to_string()]);
        // 第二条的偏移等于行首
        let text = std::str::from_utf8(data).unwrap();
        let off = text.find("   cont").unwrap() as i64;
        assert_eq!(payload.offs[1], off);
    }

    #[test]
    fn index_bom_stripped_on_first_line() {
        let mut data = vec![0xEF, 0xBB, 0xBF];
        data.extend_from_slice(b"11-14 12:00:01.123  1  2 I BOM: x\n");
        let payload = parse_bytes(&data);
        assert_eq!(payload.row_count(), 1);
        assert_eq!(payload.offs[0], 3);
        assert_eq!(payload.lens[0], 33);
    }

    #[test]
    fn time_range_threadtime() {
        let line = b"11-14 12:00:01.123  1234  5678 I MyTag: hello";
        assert_eq!(time_range(line), (0, 18));
        assert_eq!(&line[..18], b"11-14 12:00:01.123");
    }

    #[test]
    fn time_range_year_threadtime() {
        let line = b"2026-11-14 12:00:01.123456  1  2 I Tag: msg";
        assert_eq!(time_range(line), (0, 26));
        assert_eq!(&line[..26], b"2026-11-14 12:00:01.123456");
    }

    #[test]
    fn time_range_long_prefix() {
        let line = b"[ 11-14 12:00:01.123  101: 202 I/Alpha ] first";
        assert_eq!(time_range(line), (2, 18));
        assert_eq!(&line[2..20], b"11-14 12:00:01.123");

        // 方括号 long 无小数秒：止于秒末位
        let line = b"[ 11-14 12:00:01  101: 202 I/Alpha ] first";
        assert_eq!(time_range(line), (2, 14));
    }

    #[test]
    fn time_range_missing_timestamp_empty() {
        assert_eq!(time_range(b"E/Tag( 123): msg"), (0, 0));
        assert_eq!(time_range(b"    continuation line"), (0, 0));
        assert_eq!(time_range(b""), (0, 0));
        // 月越界：形似时间戳但字段非法，则不给区间
        assert_eq!(time_range(b"13-14 12:00:01.123  1  2 I T: x"), (0, 0));
    }

    #[test]
    fn time_range_malformed_shapes_empty() {
        for line in [
            &b"11-1412:00:01.123  1  2 I T: x"[..],
            b"11-14-12:00:01.123  1  2 I T: x",
            b"11-14 1200:01.123  1  2 I T: x",
            b"11-14 12-00:01.123  1  2 I T: x",
            b"11-14 12:00-01.123  1  2 I T: x",
            b"11-14 12:00:0x.123  1  2 I T: x",
            b"11-14 12:00:01.1234567890  1  2 I T: x",
            b"11-14 12:00:01. 1  2 I T: x",
            b"11-14 12:00:01.123x 1  2 I T: x",
        ] {
            assert_eq!(
                time_range(line),
                (0, 0),
                "畸形时间戳不给区间：{}",
                String::from_utf8_lossy(line)
            );
        }
    }

    #[test]
    fn time_range_independent_of_metadata() {
        // 合法时间戳、其后没有任何可识别的 pid/tid/级别/tag
        let line = b"11-14 12:00:01.123";
        assert_eq!(time_range(line), (0, 18));
        let meta = parse_line(line);
        assert_eq!(meta.pid, -1);
        assert_eq!(meta.tid, -1);
        assert_eq!(meta.level, 0);
        assert_eq!(meta.tag, None);
        assert_eq!(meta.msg_offset, 0, "元数据未知时消息取整行");
    }

    #[test]
    fn time_range_sample_files() {
        for line in include_str!("../testdata/long_sample.txt")
            .lines()
            .filter(|line| !line.is_empty())
        {
            let (start, len) = time_range(line.as_bytes());
            assert!(len > 0, "long 样本行应有时间戳：{line}");
            let text = &line.as_bytes()[start as usize..(start + len) as usize];
            assert!(
                text.first().is_some_and(u8::is_ascii_digit) && text.contains(&b':'),
                "切片应形如时间文本：{line}"
            );
        }
        for line in include_str!("../testdata/crash_sample.txt")
            .lines()
            .filter(|line| line.as_bytes().first().is_some_and(u8::is_ascii_digit))
        {
            let (_, len) = time_range(line.as_bytes());
            assert!(len > 0, "threadtime 样本行应有时间戳：{line}");
        }
        // 1~9 位小数秒，点与逗号两种分隔
        for digits in 1..=9usize {
            for sep in ['.', ','] {
                let line = format!("11-14 12:00:01{sep}{}  1  2 I Tag: msg", "1".repeat(digits));
                assert_eq!(
                    time_range(line.as_bytes()),
                    (0, 15 + digits as u32),
                    "{digits} 位小数秒（{sep}）"
                );
            }
        }
        // 带年份 + 1~9 位小数秒
        for digits in 1..=9usize {
            let line = format!(
                "2026-11-14 12:00:01.{}  1  2 I Tag: msg",
                "2".repeat(digits)
            );
            assert_eq!(
                time_range(line.as_bytes()),
                (0, 20 + digits as u32),
                "带年份 {digits} 位小数秒"
            );
        }
    }
}