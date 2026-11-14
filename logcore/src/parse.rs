//! logcat 行解析与索引增量（与 C# `LogParser` / `LogDocument` 的索引口径对齐）。
//!
//! 行口径：空行与横幅行不进索引，其余物理行各成一条记录；无法识别的行保留为
//! 「元数据未知」记录（pid/tid = -1、level = 0、分钟 = 255、无 tag），不丢行。
//! 崩溃事件提取不在本切片（D20 的读取面亦未开放），`Payload::crashes` 保持为空。

use std::collections::HashMap;

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
            meta.minute = parse_uint(&line[9..11])
                .filter(|m| (0..=59).contains(m))
                .map(|m| m as u8)
                .unwrap_or(MINUTE_NONE);
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
            meta.minute = parse_uint(&line[14..16])
                .filter(|m| (0..=59).contains(m))
                .map(|m| m as u8)
                .unwrap_or(MINUTE_NONE);
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

/// 一条源的索引：逐行追加的列段 + tag 字典（与 sidecar 载荷同构）。
#[derive(Default)]
pub struct Indexer {
    payload: Payload,
    tag_ids: HashMap<String, i32>,
}

impl Indexer {
    pub fn new() -> Self {
        Self::default()
    }

    /// 追加一行；空行 / 横幅返回 false（不成记录）。`offset` 为该行在文档中的起始字节。
    pub fn add_line(&mut self, line: &[u8], offset: u64) -> bool {
        let len = line.len();
        if len == 0 || is_banner(line) {
            return false;
        }
        let meta = parse_line(line);
        let moff = meta.msg_offset.min(len) as i32;
        self.payload.offs.push(offset as i64);
        self.payload.lens.push(len as i32);
        self.payload.moff.push(moff);
        self.payload.minutes.push(meta.minute);
        self.payload.pids.push(meta.pid);
        self.payload.tids.push(meta.tid);
        self.payload.lvls.push(meta.level);
        let tag_id = match meta.tag {
            None => -1,
            Some(tag) => match self.tag_ids.get(&tag) {
                Some(id) => *id,
                None => {
                    let id = self.payload.tags.len() as i32;
                    self.payload.tags.push(tag.clone());
                    self.tag_ids.insert(tag, id);
                    id
                }
            },
        };
        self.payload.tag_ids.push(tag_id);
        true
    }

    pub fn row_count(&self) -> u64 {
        self.payload.offs.len() as u64
    }

    pub fn payload(&self) -> &Payload {
        &self.payload
    }

    pub fn into_payload(self) -> Payload {
        self.payload
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
}