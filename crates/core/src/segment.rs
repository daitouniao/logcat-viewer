//! 段化列存与原子追加发布（D4）。
//!
//! 每块装配成不可变 [`Segment`]（按行区间持有 8 列），由 [`Store`] 统一维护
//! tag / 异常类型 / 包名三本字典与崩溃事件表；装载侧把冻结的段追加发布到
//! [`LiveIndex`]（`ArcSwap<Vec<Arc<Segment>>>`），读侧按行号定位段。
//! 崩溃前瞻的待定窗口跨段保留，因此分段结果与整份解析逐位一致。

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use arc_swap::ArcSwap;

use crate::crash;
use crate::parse::{is_banner, parse_line};
use crate::sidecar::{CrashEntry, Payload};

pub const FIRST_CHUNK_BYTES: usize = 4 * 1024 * 1024;
pub const STEADY_CHUNK_BYTES: usize = 32 * 1024 * 1024;

const LOOKAHEAD: usize = 3;

/// 不可变列段：按行区间持有 8 列。
#[derive(Default)]
pub struct Segment {
    pub start_row: usize,
    pub offs: Vec<i64>,
    pub lens: Vec<i32>,
    pub moff: Vec<i32>,
    pub minutes: Vec<u8>,
    pub pids: Vec<i32>,
    pub tids: Vec<i32>,
    pub lvls: Vec<u8>,
    pub tag_ids: Vec<i32>,
}

impl Segment {
    pub fn row_count(&self) -> usize {
        self.offs.len()
    }
}

/// 崩溃头待定窗口：等最长 3 行前瞻齐后一次性提取（可跨段）。
struct PendingCrash {
    row: i32,
    pid: i32,
    tid: i32,
    lines: Vec<String>,
}

/// 段装配器：逐行追加列，按块冻结成段；字典与崩溃表全局共享。
#[derive(Default)]
pub struct Store {
    tags: Vec<String>,
    tag_map: HashMap<String, i32>,
    exc_types: Vec<String>,
    pkgs: Vec<String>,
    crashes: Vec<CrashEntry>,
    pending: Vec<PendingCrash>,
    frozen: Vec<Arc<Segment>>,
    current: Segment,
    rows: usize,
    seg_start: usize,
}

impl Store {
    pub fn new() -> Self {
        Self::default()
    }

    /// 已索引行数（跨段累计）。
    pub fn rows(&self) -> usize {
        self.rows
    }

    /// 追加一行；空行 / 横幅返回 false（不成记录）。`offset` 为该行在文档中的起始字节。
    pub fn push_line(&mut self, line: &[u8], offset: u64) -> bool {
        self.feed_pending(line);
        let len = line.len();
        if len == 0 || is_banner(line) {
            return false;
        }
        let meta = parse_line(line);
        let moff = meta.msg_offset.min(len) as i32;
        self.current.offs.push(offset as i64);
        self.current.lens.push(len as i32);
        self.current.moff.push(moff);
        self.current.minutes.push(meta.minute);
        self.current.pids.push(meta.pid);
        self.current.tids.push(meta.tid);
        self.current.lvls.push(meta.level);
        let tag_id = match meta.tag {
            None => -1,
            Some(tag) => match self.tag_map.get(&tag) {
                Some(id) => *id,
                None => {
                    let id = self.tags.len() as i32;
                    self.tags.push(tag.clone());
                    self.tag_map.insert(tag, id);
                    id
                }
            },
        };
        self.current.tag_ids.push(tag_id);
        self.rows += 1;
        if crash::contains_keyword(line) {
            self.pending.push(PendingCrash {
                row: (self.rows - 1) as i32,
                pid: meta.pid,
                tid: meta.tid,
                lines: vec![msg_text(line, meta.msg_offset)],
            });
        }
        true
    }

    /// 冻结当前段并返回（空段返回 None）。
    pub fn freeze(&mut self) -> Option<Arc<Segment>> {        if self.current.offs.is_empty() {
            return None;
        }
        let mut seg = std::mem::take(&mut self.current);
        seg.start_row = self.seg_start;
        self.seg_start = self.rows;
        let arc = Arc::new(seg);
        self.frozen.push(Arc::clone(&arc));
        Some(arc)
    }

    /// 已冻结的段（行序）。
    pub fn frozen(&self) -> &[Arc<Segment>] {
        &self.frozen
    }

    /// 装载结束（真 EOF）时收尾：前瞻不足 3 行的崩溃头也在此提取。
    pub fn finish_pending(&mut self) {
        for p in std::mem::take(&mut self.pending) {
            self.finish_crash(p);
        }
    }

    /// 收尾并同时给出整份 `Payload` 与全部段（段化结果 ≡ 整份结果）。
    pub fn into_parts(mut self) -> (Payload, Vec<Arc<Segment>>) {
        self.finish_pending();
        self.freeze();
        let segments = std::mem::take(&mut self.frozen);
        let mut payload = Payload::default();
        for s in &segments {
            payload.offs.extend_from_slice(&s.offs);
            payload.lens.extend_from_slice(&s.lens);
            payload.moff.extend_from_slice(&s.moff);
            payload.minutes.extend_from_slice(&s.minutes);
            payload.pids.extend_from_slice(&s.pids);
            payload.tids.extend_from_slice(&s.tids);
            payload.lvls.extend_from_slice(&s.lvls);
            payload.tag_ids.extend_from_slice(&s.tag_ids);
        }
        payload.tags = self.tags;
        payload.exc_types = self.exc_types;
        payload.pkgs = self.pkgs;
        payload.crashes = self.crashes;
        (payload, segments)
    }

    pub fn into_payload(self) -> Payload {
        self.into_parts().0
    }

    fn feed_pending(&mut self, line: &[u8]) {
        if self.pending.is_empty() {
            return;
        }
        let text = line_text(line);
        for p in &mut self.pending {
            if p.lines.len() < LOOKAHEAD + 1 {
                p.lines.push(text.clone());
            }
        }
        let ready = std::mem::take(&mut self.pending);
        for p in ready {
            if p.lines.len() == LOOKAHEAD + 1 {
                self.finish_crash(p);
            } else {
                self.pending.push(p);
            }
        }
    }

    fn finish_crash(&mut self, p: PendingCrash) {
        let refs: Vec<&str> = p.lines.iter().map(String::as_str).collect();
        let Some(m) = crash::try_extract(&refs) else {
            return;
        };
        let exc_id = intern(&mut self.exc_types, m.exc);
        let pkg_id = intern(&mut self.pkgs, m.pkg);
        self.crashes.push(CrashEntry {
            row: p.row,
            exc_id,
            pkg_id,
            pid: p.pid,
            tid: p.tid,
            kind: m.kind,
        });
    }
}

/// 追加发布的段列表：装载线程持写端，读侧取快照按行号定位段。
#[derive(Default)]
pub struct LiveIndex {
    segments: ArcSwap<Vec<Arc<Segment>>>,
    rows: AtomicU64,
}

impl LiveIndex {
    pub fn new() -> Self {
        Self::default()
    }

    /// 整份替换为给定段列表并更新行数（调用方按行序传入）。
    /// 先发布段、后发布行数：读侧按「行数 → 快照」读取时，绝不会看到行数超出可见段。
    pub fn publish(&self, segments: Vec<Arc<Segment>>) {
        let rows = segments.iter().map(|s| s.row_count() as u64).sum();
        self.segments.store(Arc::new(segments));
        self.rows.store(rows, Ordering::SeqCst);
    }

    pub fn rows(&self) -> u64 {
        self.rows.load(Ordering::SeqCst)
    }

    pub fn snapshot(&self) -> Arc<Vec<Arc<Segment>>> {
        self.segments.load_full()
    }

    pub fn segment_count(&self) -> usize {
        self.segments.load().len()
    }
}

/// 取消息文本：`msg_offset` 之后；0 取整行；越界取空（与 C# `MsgTextOfLine` 同口径）。
fn msg_text(line: &[u8], msg_offset: usize) -> String {
    let len = line.len();
    if msg_offset >= len {
        return String::new();
    }
    let text = if msg_offset > 0 {
        &line[msg_offset..]
    } else {
        line
    };
    String::from_utf8_lossy(text).into_owned()
}

fn line_text(line: &[u8]) -> String {
    if line.is_empty() {
        return String::new();
    }
    msg_text(line, parse_line(line).msg_offset)
}

/// 字符串入字典：已存在返回旧 id，否则追加；空不下发（返回 -1）。
fn intern(dict: &mut Vec<String>, value: Option<String>) -> i32 {
    let Some(v) = value else {
        return -1;
    };
    match dict.iter().position(|s| *s == v) {
        Some(i) => i as i32,
        None => {
            dict.push(v);
            (dict.len() - 1) as i32
        }
    }
}

/// 按块（首块 `first`、稳态 `steady` 字节）装载整份字节：块边界对齐换行，
/// 崩溃前瞻窗口跨块保留；结果必须与 [`crate::parse::parse_bytes`] 逐位一致。
pub fn parse_chunked(data: &[u8], first: usize, steady: usize) -> (Payload, Vec<Arc<Segment>>) {
    let mut store = Store::new();
    for (start, end) in chunk_ranges(data, first, steady) {
        feed_range(&mut store, data, start, end);
        store.freeze();
    }
    store.into_parts()
}

/// 块区间：首块取 `first`，其后取 `steady`；每块向右扩到下一个换行之后（含）。
pub fn chunk_ranges(data: &[u8], first: usize, steady: usize) -> Vec<(usize, usize)> {
    let mut out = Vec::new();
    let mut pos = 0usize;
    let mut limit = first.max(1);
    while pos < data.len() {
        let mut end = pos.saturating_add(limit).min(data.len());
        if end < data.len() {
            end = match data[end..].iter().position(|b| *b == b'\n') {
                Some(i) => end + i + 1,
                None => data.len(),
            };
        }
        out.push((pos, end));
        pos = end;
        limit = steady.max(1);
    }
    out
}

/// 把 `[start, end)` 内的完整行喂给段装配器；`start == 0` 时剥首行 UTF-8 BOM。
pub fn feed_range(store: &mut Store, data: &[u8], start: usize, end: usize) {
    let mut pos = start;
    let mut first = start == 0;
    while pos < end {
        let nl = data[pos..end]
            .iter()
            .position(|b| *b == b'\n')
            .map(|i| pos + i);
        let line_end = nl.unwrap_or(end);
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
        store.push_line(line, line_off as u64);
        pos = match nl {
            Some(n) => n + 1,
            None => end,
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn publish_never_reports_rows_beyond_visible_segments() {
        use std::sync::atomic::AtomicBool;

        let live = LiveIndex::new();
        let stop = AtomicBool::new(false);
        std::thread::scope(|s| {
            s.spawn(|| {
                for total in 1..=2_000u64 {
                    let mut segs = Vec::new();
                    let mut made = 0u64;
                    while made < total {
                        let take = (total - made).min(7) as usize;
                        segs.push(Arc::new(Segment {
                            offs: vec![0; take],
                            ..Default::default()
                        }));
                        made += take as u64;
                    }
                    live.publish(segs);
                }
                stop.store(true, Ordering::SeqCst);
            });
            while !stop.load(Ordering::SeqCst) {
                let rows = live.rows();
                let visible: u64 = live
                    .snapshot()
                    .iter()
                    .map(|s| s.row_count() as u64)
                    .sum();
                assert!(rows <= visible, "rows {rows} > visible {visible}");
            }
        });
    }

    #[test]
    fn chunked_bom_stripped_on_first_line() {
        let mut data = vec![0xEF, 0xBB, 0xBF];
        data.extend_from_slice(b"11-14 12:00:01.123  1  2 I BOM: x\n");
        data.extend_from_slice(b"11-14 12:00:02.000  3  4 I BOM: y\n");

        let whole = crate::parse::parse_bytes(&data);
        let (chunked, segments) = parse_chunked(&data, 7, 5);
        assert_eq!(chunked, whole, "分块 ≡ 整份（含 BOM）");
        assert_eq!(chunked.row_count(), 2);
        assert_eq!(chunked.offs[0], 3, "首行剥离 BOM 后从字节 3 起");
        let rows: usize = segments.iter().map(|s| s.row_count()).sum();
        assert_eq!(rows, 2);
    }

    #[test]
    fn msg_text_out_of_range_is_empty() {
        assert_eq!(msg_text(b"Tag: msg", 0), "Tag: msg");
        assert_eq!(msg_text(b"Tag: msg", 5), "msg");
        assert_eq!(msg_text(b"Tag: msg", 8), "");
        assert_eq!(msg_text(b"Tag: msg", 9), "");
    }

    #[test]
    fn chunk_boundary_alignment() {
        let mut data = Vec::new();
        for i in 0..40 {
            let line = format!(
                "11-14 12:00:{:02}.123  1  2 I T{}: line {i}\n",
                i % 60,
                i % 3
            );
            data.extend_from_slice(line.as_bytes());
        }
        // 崩溃块紧贴末尾，分块后会落在首块 / 稳态块边界附近
        data.extend_from_slice(
            b"11-14 12:00:50.000  100  200 E R: FATAL EXCEPTION: main\n\
11-14 12:00:50.001  100  200 E R: Process: com.example.app, PID: 100\n\
11-14 12:00:50.002  100  200 E R: java.lang.RuntimeException: boom\n",
        );

        let whole = crate::parse::parse_bytes(&data);
        for (first, steady) in [(7, 5), (64, 32), (1000, 17)] {
            let (chunked, segments) = parse_chunked(&data, first, steady);
            assert_eq!(
                chunked, whole,
                "分块 ≡ 整份（first={first}, steady={steady}）"
            );
            let mut expect_start = 0;
            for s in &segments {
                assert_eq!(s.start_row, expect_start, "段起始行连续");
                expect_start += s.row_count();
            }
            assert_eq!(expect_start, whole.row_count(), "段覆盖全部行");
        }
    }
}
