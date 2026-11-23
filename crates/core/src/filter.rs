//! 过滤条件模型、编译与扫描。
//!
//! 条件按类别相交；结构化列先筛，正文仅在必要时按块回读。
//! 结果以升序文档行号表返回；无有效条件时返回恒等视图。

use std::collections::HashSet;
use std::fs::File;

use serde::Deserialize;

use crate::error::{io_error, CoreError, CoreResult};
use crate::identity::read_exact_at;
use crate::result::ResultSet;
use crate::sidecar::Payload;

pub const DEFAULT_CHUNK_BYTES: usize = 256 * 1024;

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FilterExpr {
    #[serde(default)]
    pub levels: Option<Vec<i32>>,
    #[serde(default)]
    pub pids: Option<ValueFilter>,
    #[serde(default)]
    pub tids: Option<ValueFilter>,
    #[serde(default)]
    pub minutes: Option<Vec<i32>>,
    #[serde(default)]
    pub tags: Option<TagFilter>,
    #[serde(default)]
    pub msg: Option<MsgFilter>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ValueFilter {
    #[serde(default)]
    pub values: Vec<i32>,
    #[serde(default)]
    pub exclude: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TagFilter {
    #[serde(default)]
    pub values: Vec<String>,
    #[serde(rename = "match", default)]
    pub match_mode: Option<String>,
    #[serde(default)]
    pub op: Option<String>,
    #[serde(default)]
    pub case: bool,
    #[serde(default)]
    pub exclude: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct MsgFilter {
    #[serde(default)]
    pub values: Vec<String>,
    #[serde(default)]
    pub mode: Option<String>,
    #[serde(default)]
    pub op: Option<String>,
    #[serde(default)]
    pub case: bool,
    #[serde(default)]
    pub exclude: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum MatchMode {
    Exact,
    Substring,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum BoolOp {
    And,
    Or,
}

#[derive(Clone, Debug)]
pub struct CompiledFilter {
    levels: Option<[bool; 8]>,
    minutes: Option<[bool; 60]>,
    pids: Option<(HashSet<i32>, bool)>,
    tids: Option<(HashSet<i32>, bool)>,
    tags: Option<CompiledTag>,
    msg: Option<CompiledMsg>,
}

#[derive(Clone, Debug)]
struct CompiledTag {
    values: Vec<String>,
    match_mode: MatchMode,
    op: BoolOp,
    case: bool,
    exclude: bool,
}

#[derive(Clone, Debug)]
struct CompiledMsg {
    needles: Vec<Vec<u8>>,
    op: BoolOp,
    case: bool,
    exclude: bool,
    always_true: bool,
}

impl FilterExpr {
    pub fn from_json(json: &str) -> CoreResult<Self> {
        let expr: Self =
            serde_json::from_str(json).map_err(|_| CoreError::invalid_arg("filter"))?;
        expr.validate()?;
        Ok(expr)
    }

    fn validate(&self) -> CoreResult<()> {
        if let Some(levels) = &self.levels {
            if levels.iter().any(|&level| !(1..=7).contains(&level)) {
                return Err(CoreError::invalid_arg("levels"));
            }
        }
        if let Some(minutes) = &self.minutes {
            if minutes.iter().any(|&minute| !(0..=59).contains(&minute)) {
                return Err(CoreError::invalid_arg("minutes"));
            }
        }
        if let Some(pids) = &self.pids {
            if pids.values.iter().any(|&pid| pid < 0) {
                return Err(CoreError::invalid_arg("pids.values"));
            }
        }
        if let Some(tids) = &self.tids {
            if tids.values.iter().any(|&tid| tid < 0) {
                return Err(CoreError::invalid_arg("tids.values"));
            }
        }
        if let Some(tags) = &self.tags {
            if let Some(mode) = tags.match_mode.as_deref() {
                if mode != "substring" && mode != "exact" {
                    return Err(CoreError::invalid_arg("tags.match"));
                }
            }
            if let Some(op) = tags.op.as_deref() {
                if op != "and" && op != "or" {
                    return Err(CoreError::invalid_arg("tags.op"));
                }
            }
            if tags.values.iter().any(String::is_empty) {
                return Err(CoreError::invalid_arg("tags.values"));
            }
        }
        if let Some(msg) = &self.msg {
            if let Some(mode) = msg.mode.as_deref() {
                if mode != "substring" {
                    return Err(CoreError::invalid_arg("msg.mode"));
                }
            }
            if let Some(op) = msg.op.as_deref() {
                if op != "and" && op != "or" {
                    return Err(CoreError::invalid_arg("msg.op"));
                }
            }
        }
        Ok(())
    }

    pub fn normalized_is_empty(&self) -> bool {
        let levels_disabled = match &self.levels {
            None => true,
            Some(levels) => {
                if levels.is_empty() {
                    true
                } else {
                    let mut table = [false; 8];
                    for &level in levels {
                        if (1..=7).contains(&level) {
                            table[level as usize] = true;
                        }
                    }
                    (1..=7).all(|level| table[level])
                }
            }
        };
        levels_disabled
            && self.minutes.as_ref().map_or(true, Vec::is_empty)
            && self.pids.as_ref().map_or(true, |pids| pids.values.is_empty())
            && self.tids.as_ref().map_or(true, |tids| tids.values.is_empty())
            && self.tags.as_ref().map_or(true, |tags| tags.values.is_empty())
            && self.msg.as_ref().map_or(true, |msg| msg.values.is_empty())
    }

    pub fn compile(&self) -> CoreResult<CompiledFilter> {
        self.validate()?;
        let levels = self.levels.as_ref().and_then(|levels| {
            if levels.is_empty() {
                return None;
            }
            let mut table = [false; 8];
            for &level in levels {
                table[level as usize] = true;
            }
            if (1..=7).all(|level| table[level]) {
                None
            } else {
                Some(table)
            }
        });
        let minutes = self.minutes.as_ref().and_then(|minutes| {
            if minutes.is_empty() {
                return None;
            }
            let mut table = [false; 60];
            for &minute in minutes {
                table[minute as usize] = true;
            }
            Some(table)
        });
        let pids = self.pids.as_ref().and_then(|pids| {
            if pids.values.is_empty() {
                None
            } else {
                Some((
                    pids.values.iter().copied().collect::<HashSet<_>>(),
                    pids.exclude,
                ))
            }
        });
        let tids = self.tids.as_ref().and_then(|tids| {
            if tids.values.is_empty() {
                None
            } else {
                Some((
                    tids.values.iter().copied().collect::<HashSet<_>>(),
                    tids.exclude,
                ))
            }
        });
        let tags = self.tags.as_ref().and_then(|tags| {
            if tags.values.is_empty() {
                return None;
            }
            let values = if tags.case {
                tags.values.clone()
            } else {
                tags.values.iter().map(|value| value.to_lowercase()).collect()
            };
            Some(CompiledTag {
                values,
                match_mode: parse_match_mode(tags.match_mode.as_deref()),
                op: parse_bool_op(tags.op.as_deref(), BoolOp::Or),
                case: tags.case,
                exclude: tags.exclude,
            })
        });
        let msg = self.msg.as_ref().and_then(|msg| {
            if msg.values.is_empty() {
                None
            } else {
                Some(CompiledMsg::new(
                    &msg.values,
                    parse_bool_op(msg.op.as_deref(), BoolOp::And),
                    msg.case,
                    msg.exclude,
                ))
            }
        });
        Ok(CompiledFilter {
            levels,
            minutes,
            pids,
            tids,
            tags,
            msg,
        })
    }
}

impl CompiledFilter {
    pub fn is_identity(&self) -> bool {
        self.levels.is_none()
            && self.minutes.is_none()
            && self.pids.is_none()
            && self.tids.is_none()
            && self.tags.is_none()
            && self.msg.is_none()
    }
}

impl CompiledTag {
    fn hit_ids(&self, dict: &[String]) -> HashSet<i32> {
        let mut hits = HashSet::new();
        for (id, item) in dict.iter().enumerate() {
            let item = if self.case {
                item.clone()
            } else {
                item.to_lowercase()
            };
            let matched = match self.op {
                BoolOp::And => self
                    .values
                    .iter()
                    .all(|value| self.value_matches(&item, value)),
                BoolOp::Or => self
                    .values
                    .iter()
                    .any(|value| self.value_matches(&item, value)),
            };
            if matched {
                hits.insert(id as i32);
            }
        }
        hits
    }

    fn value_matches(&self, item: &str, value: &str) -> bool {
        match self.match_mode {
            MatchMode::Exact => item == value,
            MatchMode::Substring => item.contains(value),
        }
    }
}

impl CompiledMsg {
    fn new(values: &[String], op: BoolOp, case: bool, exclude: bool) -> Self {
        let needles: Vec<Vec<u8>> = values
            .iter()
            .map(|value| {
                if case {
                    value.as_bytes().to_vec()
                } else {
                    value.to_lowercase().into_bytes()
                }
            })
            .collect();
        let has_empty = needles.iter().any(Vec::is_empty);
        let all_empty = needles.iter().all(Vec::is_empty);
        let always_true = match op {
            BoolOp::Or => has_empty,
            BoolOp::And => all_empty,
        };
        Self {
            needles,
            op,
            case,
            exclude,
            always_true,
        }
    }

    fn matches(&self, body: &[u8]) -> bool {
        if self.always_true {
            return true;
        }
        match self.op {
            BoolOp::And => self
                .needles
                .iter()
                .filter(|needle| !needle.is_empty())
                .all(|needle| needle_matches(needle, body, self.case)),
            BoolOp::Or => self
                .needles
                .iter()
                .filter(|needle| !needle.is_empty())
                .any(|needle| needle_matches(needle, body, self.case)),
        }
    }
}

fn parse_match_mode(value: Option<&str>) -> MatchMode {
    match value {
        Some("exact") => MatchMode::Exact,
        _ => MatchMode::Substring,
    }
}

fn parse_bool_op(value: Option<&str>, default: BoolOp) -> BoolOp {
    match value {
        Some("and") => BoolOp::And,
        Some("or") => BoolOp::Or,
        _ => default,
    }
}

fn needle_matches(needle: &[u8], body: &[u8], case_sensitive: bool) -> bool {
    if needle.is_empty() {
        return true;
    }
    if case_sensitive {
        return contains_bytes(body, needle);
    }
    if needle.is_ascii() {
        if body.is_ascii() {
            contains_ascii_ci(body, needle)
        } else {
            contains_folded(body, needle)
        }
    } else if body.is_ascii() {
        false
    } else {
        contains_folded(body, needle)
    }
}

fn contains_bytes(hay: &[u8], needle: &[u8]) -> bool {
    needle.len() <= hay.len() && hay.windows(needle.len()).any(|window| window == needle)
}

fn contains_ascii_ci(hay: &[u8], needle: &[u8]) -> bool {
    needle.len() <= hay.len()
        && hay
            .windows(needle.len())
            .any(|window| window.eq_ignore_ascii_case(needle))
}

fn contains_folded(body: &[u8], needle: &[u8]) -> bool {
    let folded = String::from_utf8_lossy(body).to_lowercase();
    contains_bytes(folded.as_bytes(), needle)
}

pub fn scan(
    payload: &Payload,
    file: &File,
    display: &str,
    filter: &CompiledFilter,
) -> CoreResult<ResultSet> {
    let mut stats = ReadStats::default();
    scan_inner(
        payload,
        file,
        display,
        filter,
        DEFAULT_CHUNK_BYTES,
        &mut stats,
    )
}

fn scan_inner(
    payload: &Payload,
    file: &File,
    display: &str,
    filter: &CompiledFilter,
    chunk: usize,
    stats: &mut ReadStats,
) -> CoreResult<ResultSet> {
    let row_count = payload.row_count();
    debug_assert!(row_count <= u32::MAX as usize, "文档行数超出 u32");
    let doc_count = row_count as u32;
    if filter.is_identity() {
        return Ok(ResultSet::identity(doc_count));
    }

    let tag_hits = filter
        .tags
        .as_ref()
        .map(|tag| tag.hit_ids(&payload.tags));
    let doc_end = doc_end(payload);
    let mut reader = ChunkReader::new(file, chunk.max(1), stats);
    let mut rows = Vec::new();

    for row in 0..row_count {
        if let Some(levels) = &filter.levels {
            let level = payload.lvls.get(row).copied().unwrap_or(0) as usize;
            if !levels.get(level).copied().unwrap_or(false) {
                continue;
            }
        }
        if let Some((values, exclude)) = &filter.pids {
            let pid = payload.pids.get(row).copied().unwrap_or(-1);
            if values.contains(&pid) == *exclude {
                continue;
            }
        }
        if let Some((values, exclude)) = &filter.tids {
            let tid = payload.tids.get(row).copied().unwrap_or(-1);
            if values.contains(&tid) == *exclude {
                continue;
            }
        }
        if let Some(minutes) = &filter.minutes {
            let minute = payload.minutes.get(row).copied().unwrap_or(255);
            if minute >= 60 || !minutes[minute as usize] {
                continue;
            }
        }
        if let (Some(tag), Some(hits)) = (&filter.tags, &tag_hits) {
            let id = payload.tag_ids.get(row).copied().unwrap_or(-1);
            let hit = id >= 0 && hits.contains(&id);
            if hit == tag.exclude {
                continue;
            }
        }
        if let Some(msg) = &filter.msg {
            let hit = if msg.always_true {
                true
            } else {
                let (start, len) = body_range(payload, row);
                if len == 0 {
                    msg.matches(&[])
                } else {
                    let body = reader
                        .read(start, len, doc_end)
                        .map_err(|e| io_error(display.to_string(), &e))?;
                    msg.matches(body)
                }
            };
            if hit == msg.exclude {
                continue;
            }
        }
        rows.push(row as u32);
    }

    Ok(ResultSet::from_rows(rows, doc_count))
}

fn doc_end(payload: &Payload) -> u64 {
    let mut end = 0u64;
    for row in 0..payload.row_count() {
        let offset = payload.offs.get(row).copied().unwrap_or(0).max(0) as u64;
        let len = payload.lens.get(row).copied().unwrap_or(0).max(0) as u64;
        end = end.max(offset.saturating_add(len));
    }
    end
}

fn body_range(payload: &Payload, row: usize) -> (u64, usize) {
    let offset = payload.offs.get(row).copied().unwrap_or(0).max(0) as u64;
    let len = payload.lens.get(row).copied().unwrap_or(0).max(0) as usize;
    let moff = payload.moff.get(row).copied().unwrap_or(0).max(0) as usize;
    let moff = moff.min(len);
    (offset.saturating_add(moff as u64), len - moff)
}

#[derive(Default)]
#[allow(dead_code)]
struct ReadStats {
    reads: usize,
    bytes: usize,
    last_start: u64,
}

struct ChunkReader<'a> {
    file: &'a File,
    chunk: usize,
    buf: Vec<u8>,
    start: u64,
    end: u64,
    stats: &'a mut ReadStats,
}

impl<'a> ChunkReader<'a> {
    fn new(file: &'a File, chunk: usize, stats: &'a mut ReadStats) -> Self {
        Self {
            file,
            chunk,
            buf: Vec::new(),
            start: 0,
            end: 0,
            stats,
        }
    }

    fn read(&mut self, start: u64, len: usize, doc_end: u64) -> std::io::Result<&[u8]> {
        let end = start.saturating_add(len as u64);
        if start < self.start || end > self.end {
            let want = (len as u64).max(self.chunk as u64);
            let read_len = want.min(doc_end.saturating_sub(start)) as usize;
            self.buf.resize(read_len, 0);
            read_exact_at(self.file, &mut self.buf, start)?;
            self.start = start;
            self.end = start.saturating_add(read_len as u64);
            self.stats.reads += 1;
            self.stats.bytes += read_len;
            self.stats.last_start = start;
        }
        let offset = (start - self.start) as usize;
        let slice_end = offset.saturating_add(len);
        if slice_end > self.buf.len() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "filter chunk",
            ));
        }
        Ok(&self.buf[offset..slice_end])
    }
}

#[cfg(test)]
fn scan_with_chunk(
    payload: &Payload,
    file: &File,
    display: &str,
    filter: &CompiledFilter,
    chunk: usize,
) -> CoreResult<ResultSet> {
    let mut stats = ReadStats::default();
    scan_inner(payload, file, display, filter, chunk, &mut stats)
}

#[cfg(test)]
fn scan_with_chunk_stats(
    payload: &Payload,
    file: &File,
    display: &str,
    filter: &CompiledFilter,
    chunk: usize,
) -> (CoreResult<ResultSet>, ReadStats) {
    let mut stats = ReadStats::default();
    let result = scan_inner(payload, file, display, filter, chunk, &mut stats);
    (result, stats)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{INVALID_ARG, IO_ERROR};
    use crate::page::{encode_page, page_rows_for, PageRow};
    use crate::parse::parse_bytes;
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicUsize, Ordering};

    static SEQ: AtomicUsize = AtomicUsize::new(0);

    fn temp_dir(name: &str) -> PathBuf {
        let seq = SEQ.fetch_add(1, Ordering::Relaxed);
        let dir = std::env::temp_dir().join(format!(
            "logcore-filter-{}-{}-{}",
            std::process::id(),
            name,
            seq
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    struct TestFile {
        dir: PathBuf,
        path: PathBuf,
        file: Option<std::fs::File>,
    }

    impl TestFile {
        fn new(name: &str, data: &[u8]) -> Self {
            let dir = temp_dir(name);
            let path = dir.join("a.log");
            std::fs::write(&path, data).unwrap();
            let file = std::fs::File::open(&path).unwrap();
            Self {
                dir,
                path,
                file: Some(file),
            }
        }

        fn file(&self) -> &std::fs::File {
            self.file.as_ref().unwrap()
        }

        fn path(&self) -> &Path {
            &self.path
        }

        fn dir(&self) -> &Path {
            &self.dir
        }
    }

    impl Drop for TestFile {
        fn drop(&mut self) {
            self.file.take();
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    fn expr(json: &str) -> FilterExpr {
        FilterExpr::from_json(json).unwrap()
    }

    fn compile(json: &str) -> CompiledFilter {
        FilterExpr::from_json(json).unwrap().compile().unwrap()
    }

    fn assert_invalid(json: &str, field: &str) {
        let err = FilterExpr::from_json(json).unwrap_err();
        assert_eq!(err.code, INVALID_ARG, "{json}");
        assert_eq!(
            err.params.get("field").and_then(|value| value.as_str()),
            Some(field),
            "{json}"
        );
    }

    fn rows(set: &ResultSet) -> Vec<u32> {
        (0..set.len()).map(|ord| set.doc_row(ord)).collect()
    }

    fn hits(values: &HashSet<i32>) -> Vec<i32> {
        let mut out: Vec<i32> = values.iter().copied().collect();
        out.sort_unstable();
        out
    }

    fn cols(
        lvls: &[u8],
        pids: &[i32],
        tids: &[i32],
        minutes: &[u8],
        tag_ids: &[i32],
        tags: &[&str],
    ) -> Payload {
        let n = lvls.len();
        Payload {
            offs: vec![0; n],
            lens: vec![0; n],
            moff: vec![0; n],
            minutes: minutes.to_vec(),
            pids: pids.to_vec(),
            tids: tids.to_vec(),
            lvls: lvls.to_vec(),
            tag_ids: tag_ids.to_vec(),
            tags: tags.iter().map(|tag| tag.to_string()).collect(),
            ..Default::default()
        }
    }

    fn tag_payload(tags: &[&str], tag_ids: &[i32]) -> Payload {
        let n = tag_ids.len();
        cols(
            &vec![3u8; n],
            &vec![1i32; n],
            &vec![1i32; n],
            &vec![0u8; n],
            tag_ids,
            tags,
        )
    }

    fn scan_cols(payload: &Payload, filter: &CompiledFilter) -> ResultSet {
        let tf = TestFile::new("scan-cols", b"");
        scan(payload, tf.file(), "dummy", filter).unwrap()
    }

    fn lines_data(lines: &[&str]) -> Vec<u8> {
        let mut data = Vec::new();
        for line in lines {
            data.extend_from_slice(line.as_bytes());
            data.push(b'\n');
        }
        data
    }

    fn fixture(name: &str, data: &[u8]) -> (TestFile, Payload) {
        let tf = TestFile::new(name, data);
        let payload = parse_bytes(data);
        (tf, payload)
    }

    fn body_start(payload: &Payload, row: usize) -> u64 {
        payload.offs[row].max(0) as u64 + payload.moff[row].max(0) as u64
    }

    fn forward_ord(set: &ResultSet, start: u32) -> Option<usize> {
        match set {
            ResultSet::Identity { rows } => {
                if start < *rows {
                    Some(start as usize)
                } else {
                    None
                }
            }
            ResultSet::Rows(rows) => {
                let at = rows.partition_point(|&row| row < start);
                if at == rows.len() {
                    None
                } else {
                    Some(at)
                }
            }
        }
    }

    fn forward_page(
        set: &ResultSet,
        payload: &Payload,
        start: u32,
        count: usize,
    ) -> Vec<PageRow> {
        match forward_ord(set, start) {
            Some(ord) => page_rows_for(set, payload, ord, count),
            None => Vec::new(),
        }
    }

    fn page_bytes(
        set: &ResultSet,
        payload: &Payload,
        file: &std::fs::File,
        ord: usize,
        count: usize,
    ) -> Vec<u8> {
        let rows = page_rows_for(set, payload, ord, count);
        let mut entries = Vec::new();
        for row in rows {
            let index = row.doc_row as usize;
            let offset = payload.offs[index].max(0) as u64;
            let len = payload.lens[index].max(0) as usize;
            let mut text = vec![0u8; len];
            crate::identity::read_exact_at(file, &mut text, offset).unwrap();
            entries.push(row.with_text(text));
        }
        encode_page(&entries, ord as u32)
    }

    fn ten_rows(name: &str) -> (TestFile, Vec<u8>, Payload) {
        let lines: Vec<String> = (0..10)
            .map(|i| format!("11-14 12:00:{:02}.123  100  200 I T: line {i}", i))
            .collect();
        let refs: Vec<&str> = lines.iter().map(String::as_str).collect();
        let data = lines_data(&refs);
        let (tf, payload) = fixture(name, &data);
        (tf, data, payload)
    }

    #[test]
    fn filter_expr_defaults_are_empty() {
        let parsed = expr("{}");
        assert!(parsed.normalized_is_empty());
        assert!(parsed.compile().unwrap().is_identity());
        let default = FilterExpr::default();
        assert!(default.normalized_is_empty());
        assert!(default.compile().unwrap().is_identity());
    }

    #[test]
    fn filter_expr_parses_full_expression() {
        let parsed = expr(
            r#"{
                "levels":[3,4],
                "pids":{"values":[100],"exclude":true},
                "tids":{"values":[200]},
                "minutes":[0,30],
                "tags":{"values":["Activity"],"match":"substring","op":"or","case":false,"exclude":false},
                "msg":{"values":["started"],"mode":"substring","op":"and","case":false,"exclude":false}
            }"#,
        );
        assert_eq!(parsed.levels, Some(vec![3, 4]));
        assert_eq!(parsed.minutes, Some(vec![0, 30]));
        assert_eq!(parsed.pids.as_ref().unwrap().values, vec![100]);
        assert!(parsed.pids.as_ref().unwrap().exclude);
        assert_eq!(parsed.tids.as_ref().unwrap().values, vec![200]);
        let tags = parsed.tags.as_ref().unwrap();
        assert_eq!(tags.values[0].as_str(), "Activity");
        assert_eq!(tags.match_mode.as_deref(), Some("substring"));
        assert_eq!(tags.op.as_deref(), Some("or"));
        assert!(!tags.case);
        assert!(!tags.exclude);
        let msg = parsed.msg.as_ref().unwrap();
        assert_eq!(msg.values[0].as_str(), "started");
        assert_eq!(msg.mode.as_deref(), Some("substring"));
        assert_eq!(msg.op.as_deref(), Some("and"));
        assert!(!msg.case);
        assert!(!msg.exclude);
    }

    #[test]
    fn filter_expr_parses_tag_exact_match() {
        let parsed = expr(r#"{"tags":{"values":["Alpha"],"match":"exact"}}"#);
        assert_eq!(
            parsed.tags.as_ref().unwrap().match_mode.as_deref(),
            Some("exact")
        );
    }

    #[test]
    fn filter_expr_default_match_op_mode() {
        let parsed = expr(r#"{"tags":{"values":["A"]},"msg":{"values":["x"]}}"#);
        let tags = parsed.tags.as_ref().unwrap();
        assert!(tags.match_mode.is_none());
        assert!(tags.op.is_none());
        assert!(!tags.case);
        assert!(!tags.exclude);
        let msg = parsed.msg.as_ref().unwrap();
        assert!(msg.mode.is_none());
        assert!(msg.op.is_none());
        assert!(!msg.case);
        assert!(!msg.exclude);

        let compiled = parsed.compile().unwrap();
        let tags = compiled.tags.as_ref().unwrap();
        assert_eq!(tags.match_mode, MatchMode::Substring);
        assert_eq!(tags.op, BoolOp::Or);
        let msg = compiled.msg.as_ref().unwrap();
        assert_eq!(msg.op, BoolOp::And);
        assert!(!msg.always_true);
    }

    #[test]
    fn filter_expr_rejects_unknown_field() {
        assert_invalid(r#"{"foo":1}"#, "filter");
    }

    #[test]
    fn filter_expr_rejects_type_mismatch() {
        assert_invalid(r#"{"levels":"3"}"#, "filter");
    }

    #[test]
    fn filter_expr_rejects_unsupported_category() {
        assert_invalid(r#"{"marked":[1]}"#, "filter");
    }

    #[test]
    fn filter_expr_rejects_unknown_op() {
        assert_invalid(r#"{"tags":{"values":["A"],"op":"xor"}}"#, "tags.op");
        assert_invalid(r#"{"msg":{"values":["x"],"op":"xor"}}"#, "msg.op");
    }

    #[test]
    fn filter_expr_rejects_unknown_tag_match() {
        assert_invalid(r#"{"tags":{"values":["A"],"match":"prefix"}}"#, "tags.match");
    }

    #[test]
    fn filter_expr_rejects_unknown_mode() {
        assert_invalid(r#"{"msg":{"values":["x"],"mode":"word"}}"#, "msg.mode");
    }

    #[test]
    fn filter_expr_rejects_minute_out_of_range() {
        assert_invalid(r#"{"minutes":[60]}"#, "minutes");
        assert_invalid(r#"{"minutes":[300]}"#, "minutes");
    }

    #[test]
    fn filter_expr_rejects_regex_mode() {
        assert_invalid(r#"{"msg":{"values":["x"],"mode":"regex"}}"#, "msg.mode");
    }

    #[test]
    fn filter_expr_rejects_level_out_of_range() {
        assert_invalid(r#"{"levels":[0]}"#, "levels");
        assert_invalid(r#"{"levels":[8]}"#, "levels");
        assert_invalid(r#"{"levels":[300]}"#, "levels");
    }

    #[test]
    fn filter_expr_rejects_negative_pid_tid() {
        assert_invalid(r#"{"pids":{"values":[-1]}}"#, "pids.values");
        assert_invalid(r#"{"tids":{"values":[-2]}}"#, "tids.values");
    }

    #[test]
    fn filter_expr_rejects_empty_tag_value() {
        assert_invalid(r#"{"tags":{"values":[""]}}"#, "tags.values");
    }

    #[test]
    fn compile_levels_all_equivalent_to_disabled() {
        let compiled = compile(r#"{"levels":[1,2,3,4,5,6,7]}"#);
        assert!(compiled.is_identity());
        assert!(compiled.levels.is_none());
    }

    #[test]
    fn compile_empty_pid_tid_disabled() {
        let compiled = compile(r#"{"pids":{"values":[]},"tids":{"values":[],"exclude":true}}"#);
        assert!(compiled.is_identity());
        assert!(compiled.pids.is_none());
        assert!(compiled.tids.is_none());
    }

    #[test]
    fn compile_tag_exact_and_distinct_values_empty() {
        let compiled =
            compile(r#"{"tags":{"values":["Alpha","Beta"],"match":"exact","op":"and"}}"#);
        let dict = vec!["Alpha".to_string()];
        let hit_ids = compiled.tags.as_ref().unwrap().hit_ids(&dict);
        assert!(hit_ids.is_empty());
    }

    #[test]
    fn compile_tag_exact_and_case_fold_equivalent_hits() {
        let compiled =
            compile(r#"{"tags":{"values":["Alpha","alpha"],"match":"exact","op":"and"}}"#);
        let dict = vec!["Alpha".to_string()];
        let hit_ids = compiled.tags.as_ref().unwrap().hit_ids(&dict);
        assert_eq!(hits(&hit_ids), vec![0]);
    }

    #[test]
    fn compile_tag_substring_case_fold() {
        let compiled =
            compile(r#"{"tags":{"values":["manager"],"match":"substring","op":"or"}}"#);
        let dict = vec!["ActivityManager".to_string()];
        let hit_ids = compiled.tags.as_ref().unwrap().hit_ids(&dict);
        assert_eq!(hits(&hit_ids), vec![0]);
    }

    #[test]
    fn compile_tag_exact_case_fold() {
        let compiled = compile(r#"{"tags":{"values":["alpha"],"match":"exact","op":"or"}}"#);
        let dict = vec!["Alpha".to_string()];
        let hit_ids = compiled.tags.as_ref().unwrap().hit_ids(&dict);
        assert_eq!(hits(&hit_ids), vec![0]);
    }

    #[test]
    fn filter_empty_returns_identity() {
        let payload = cols(&[1, 2], &[1, 2], &[1, 2], &[0, 1], &[-1, -1], &[]);
        let set = scan_cols(&payload, &compile("{}"));
        assert_eq!(set, ResultSet::identity(2));
        assert_eq!(rows(&set), vec![0, 1]);
    }

    #[test]
    fn filter_level_all_levels_keeps_unknown_level_row() {
        let payload = cols(
            &[1, 2, 3, 4, 5, 6, 7, 0],
            &[1; 8],
            &[1; 8],
            &[0; 8],
            &[-1; 8],
            &[],
        );
        let set = scan_cols(&payload, &compile(r#"{"levels":[1,2,3,4,5,6,7]}"#));
        assert_eq!(set, ResultSet::identity(8));
        assert_eq!(rows(&set), (0u32..8).collect::<Vec<_>>());
    }

    #[test]
    fn filter_level_set_any() {
        let payload = cols(&[2, 3, 5], &[1; 3], &[1; 3], &[0; 3], &[-1; 3], &[]);
        let set = scan_cols(&payload, &compile(r#"{"levels":[2,5]}"#));
        assert_eq!(rows(&set), vec![0, 2]);
    }

    #[test]
    fn filter_pid_exclude_keeps_unknown_pid() {
        let payload = cols(&[3; 3], &[100, 200, -1], &[1; 3], &[0; 3], &[-1; 3], &[]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"pids":{"values":[100],"exclude":true}}"#),
        );
        assert_eq!(rows(&set), vec![1, 2]);
    }

    #[test]
    fn filter_tid_exclude_keeps_unknown_tid() {
        let payload = cols(&[3; 3], &[1; 3], &[100, 200, -1], &[0; 3], &[-1; 3], &[]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tids":{"values":[100],"exclude":true}}"#),
        );
        assert_eq!(rows(&set), vec![1, 2]);
    }

    #[test]
    fn filter_minute_skips_missing_timestamp() {
        let payload = cols(&[3; 3], &[1; 3], &[1; 3], &[30, 255, 30], &[-1; 3], &[]);
        let set = scan_cols(&payload, &compile(r#"{"minutes":[30]}"#));
        assert_eq!(rows(&set), vec![0, 2]);
    }

    #[test]
    fn filter_conditions_are_anded() {
        let payload = cols(
            &[5, 5, 3],
            &[100, 200, 100],
            &[1, 1, 1],
            &[0, 0, 0],
            &[-1, -1, -1],
            &[],
        );
        let set = scan_cols(
            &payload,
            &compile(r#"{"levels":[5],"pids":{"values":[100]}}"#),
        );
        assert_eq!(rows(&set), vec![0]);
    }

    #[test]
    fn filter_tag_substring_or() {
        let payload = tag_payload(&["ActivityManager", "WindowManager", "Other"], &[0, 1, 2]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["Manager"],"match":"substring","op":"or"}}"#),
        );
        assert_eq!(rows(&set), vec![0, 1]);
    }

    #[test]
    fn filter_tag_substring_and() {
        let payload = tag_payload(&["ActivityManager"], &[0]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["Activity","Manager"],"match":"substring","op":"and"}}"#),
        );
        assert_eq!(rows(&set), vec![0]);
    }

    #[test]
    fn filter_tag_exact_or() {
        let payload = tag_payload(&["ActivityManager", "WindowManager"], &[0, 1]);
        let set = scan_cols(
            &payload,
            &compile(
                r#"{"tags":{"values":["ActivityManager","WindowManager"],"match":"exact","op":"or"}}"#,
            ),
        );
        assert_eq!(rows(&set), vec![0, 1]);
    }

    #[test]
    fn filter_tag_exact_all_values_empty() {
        let payload = tag_payload(&["Alpha"], &[0]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["Alpha","Beta"],"match":"exact","op":"and"}}"#),
        );
        assert!(rows(&set).is_empty());
    }

    #[test]
    fn filter_tag_exact_and_case_fold_equivalent_hits() {
        let payload = tag_payload(&["Alpha"], &[0]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["Alpha","alpha"],"match":"exact","op":"and"}}"#),
        );
        assert_eq!(rows(&set), vec![0]);
    }

    #[test]
    fn filter_tag_exclude_keeps_missing_tag() {
        let payload = tag_payload(&["ActivityManager"], &[0, -1]);
        let include = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["ActivityManager"]}}"#),
        );
        assert_eq!(rows(&include), vec![0], "启用时无 tag 记录不命中");
        let exclude = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["ActivityManager"],"exclude":true}}"#),
        );
        assert_eq!(rows(&exclude), vec![1]);
    }

    #[test]
    fn filter_tag_exclude_removes_hit() {
        let payload = tag_payload(&["ActivityManager", "WindowManager"], &[0, 1]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["ActivityManager"],"exclude":true}}"#),
        );
        assert_eq!(rows(&set), vec![1]);
    }

    #[test]
    fn filter_tag_case_insensitive() {
        let payload = tag_payload(&["ActivityManager"], &[0]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["manager"],"match":"substring"}}"#),
        );
        assert_eq!(rows(&set), vec![0]);
    }

    #[test]
    fn filter_tag_exact_case_fold() {
        let payload = tag_payload(&["Alpha"], &[0]);
        let set = scan_cols(
            &payload,
            &compile(r#"{"tags":{"values":["alpha"],"match":"exact"}}"#),
        );
        assert_eq!(rows(&set), vec![0]);
    }

    #[test]
    fn filter_result_rows_ascending_unique() {
        let payload = cols(&[3; 5], &[100; 5], &[1; 5], &[0; 5], &[-1; 5], &[]);
        let set = scan_cols(&payload, &compile(r#"{"pids":{"values":[100]}}"#));
        let result_rows = rows(&set);
        assert_eq!(result_rows, vec![0, 1, 2, 3, 4]);
        assert!(result_rows.windows(2).all(|pair| pair[0] < pair[1]));
    }

    #[test]
    fn filter_result_contains_only_records() {
        let data = lines_data(&[
            "--------- beginning of main",
            "11-14 12:00:01.123  100  200 I T: a",
            "",
            "11-14 12:00:02.123  100  200 I T: b",
        ]);
        let (tf, payload) = fixture("records", &data);
        assert_eq!(payload.row_count(), 2, "横幅与空行不成记录");
        let set = scan(
            &payload,
            tf.file(),
            "records",
            &compile(r#"{"pids":{"values":[100]}}"#),
        )
        .unwrap();
        assert_eq!(rows(&set), vec![0, 1]);
    }

    #[test]
    fn filter_no_hit_returns_empty_rows() {
        let payload = cols(&[3; 2], &[100, 200], &[1; 2], &[0; 2], &[-1; 2], &[]);
        let set = scan_cols(&payload, &compile(r#"{"pids":{"values":[999]}}"#));
        assert!(rows(&set).is_empty());
    }

    #[test]
    fn filter_text_spans_chunk_boundary() {
        let data = lines_data(&[
            "11-14 12:00:01.123  1  2 I T: ab",
            "11-14 12:00:02.123  1  2 I T: abcdefghijklmnop",
        ]);
        let (tf, payload) = fixture("chunk-boundary", &data);
        let first = body_start(&payload, 0);
        let second = body_start(&payload, 1);
        assert!(
            second < first + 40 && second + 16 > first + 40,
            "样本应跨 40 字节块边界"
        );
        let filter = compile(r#"{"msg":{"values":["abcdefghijklmnop"]}}"#);
        let (result, stats) =
            scan_with_chunk_stats(&payload, tf.file(), "chunk-boundary", &filter, 40);
        assert_eq!(rows(&result.unwrap()), vec![1]);
        assert_eq!(stats.reads, 2, "第一块跨到第二行正文，需二次读取");
    }

    #[test]
    fn filter_text_sparse_candidates_skip_gaps() {
        let data = lines_data(&[
            "11-14 12:00:00.000  1  2 E T: needle",
            "11-14 12:00:01.000  1  2 I T: x",
            "11-14 12:00:02.000  1  2 I T: x",
            "11-14 12:00:03.000  1  2 I T: x",
            "11-14 12:00:04.000  1  2 I T: x",
            "11-14 12:00:05.000  1  2 I T: x",
            "11-14 12:00:06.000  1  2 E T: needle",
        ]);
        let (tf, payload) = fixture("sparse", &data);
        let filter = compile(r#"{"levels":[5],"msg":{"values":["needle"]}}"#);
        let (result, stats) = scan_with_chunk_stats(&payload, tf.file(), "sparse", &filter, 8);
        assert_eq!(rows(&result.unwrap()), vec![0, 6]);
        assert_eq!(stats.reads, 2, "只读命中候选，跳过中间间隔");
    }

    #[test]
    fn filter_text_row_larger_than_chunk() {
        let data = lines_data(&["11-14 12:00:00.000  1  2 I T: abcdefghijklmnopqrstuvwxyz"]);
        let (tf, payload) = fixture("large-row", &data);
        let filter = compile(r#"{"msg":{"values":["mnop"]}}"#);
        let (result, stats) = scan_with_chunk_stats(&payload, tf.file(), "large-row", &filter, 8);
        assert_eq!(rows(&result.unwrap()), vec![0]);
        assert_eq!(stats.reads, 1);
        assert!(stats.bytes >= 26, "行大于块时按行长度读取");
    }

    #[test]
    fn filter_text_last_row_with_default_chunk() {
        let data = lines_data(&["11-14 12:00:00.000  1  2 I T: tail"]);
        let (tf, payload) = fixture("last-row", &data);
        let filter = compile(r#"{"msg":{"values":["tail"]}}"#);
        let (result, stats) =
            scan_with_chunk_stats(&payload, tf.file(), "last-row", &filter, DEFAULT_CHUNK_BYTES);
        assert_eq!(rows(&result.unwrap()), vec![0]);
        assert_eq!(stats.reads, 1);
        assert_eq!(stats.bytes, 4, "末行按 doc_end 截断，不越界读");
    }

    #[test]
    fn filter_text_reads_message_slice_only() {
        let data = lines_data(&["11-14 12:00:00.000  1  2 I Tag: body"]);
        let (tf, payload) = fixture("slice", &data);
        let filter = compile(r#"{"msg":{"values":["body"]}}"#);
        let expected_start = body_start(&payload, 0);
        let (result, stats) = scan_with_chunk_stats(&payload, tf.file(), "slice", &filter, 1);
        assert_eq!(rows(&result.unwrap()), vec![0]);
        assert_eq!(stats.last_start, expected_start, "从正文起点读，不读头部");
        assert_eq!(stats.bytes, 4);
    }

    #[test]
    fn filter_chunk_size_does_not_change_result() {
        let data = lines_data(&[
            "11-14 12:00:00.000  1  2 I T: hit",
            "11-14 12:00:01.000  1  2 I T: miss",
            "11-14 12:00:02.000  1  2 I T: hit again",
        ]);
        let (tf, payload) = fixture("chunk-size", &data);
        let filter = compile(r#"{"msg":{"values":["hit"]}}"#);
        let default = scan(&payload, tf.file(), "chunk-size", &filter).unwrap();
        let tiny = scan_with_chunk(&payload, tf.file(), "chunk-size", &filter, 1).unwrap();
        assert_eq!(default, tiny);
        assert_eq!(rows(&default), vec![0, 2]);
    }

    #[test]
    fn filter_structured_only_skips_text_read() {
        let payload = cols(&[3; 3], &[100, 200, 100], &[1; 3], &[0; 3], &[-1; 3], &[]);
        let filter = compile(r#"{"pids":{"values":[100]}}"#);
        let tf = TestFile::new("structured", b"");
        let (result, stats) =
            scan_with_chunk_stats(&payload, tf.file(), "structured", &filter, DEFAULT_CHUNK_BYTES);
        assert_eq!(rows(&result.unwrap()), vec![0, 2]);
        assert_eq!(stats.reads, 0);
    }

    #[test]
    fn filter_msg_scope_ignores_header() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I ActivityManager: hello"]);
        let (tf, payload) = fixture("scope", &data);
        let header_only = compile(r#"{"msg":{"values":["ActivityManager"]}}"#);
        assert!(rows(&scan(&payload, tf.file(), "scope", &header_only).unwrap()).is_empty());
        let body = compile(r#"{"msg":{"values":["hello"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "scope", &body).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_empty_body_with_empty_needle() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: "]);
        let (tf, payload) = fixture("empty-body-empty-needle", &data);
        let filter = compile(r#"{"msg":{"values":[""]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "empty", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_empty_body_with_nonempty_needle() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: "]);
        let (tf, payload) = fixture("empty-body-nonempty-needle", &data);
        let filter = compile(r#"{"msg":{"values":["started"]}}"#);
        assert!(rows(&scan(&payload, tf.file(), "empty", &filter).unwrap()).is_empty());
    }

    #[test]
    fn filter_msg_empty_needle_and_mode_skips() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: hello needle"]);
        let (tf, payload) = fixture("empty-and", &data);
        let filter = compile(r#"{"msg":{"values":["","needle"],"op":"and"}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "empty-and", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_continuation_matches_itself() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: parent",
            "    Foo.bar",
        ]);
        let (tf, payload) = fixture("continuation", &data);
        let filter = compile(r#"{"msg":{"values":["Foo.bar"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "continuation", &filter).unwrap()),
            vec![1]
        );
    }

    #[test]
    fn filter_msg_case_sensitive() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: PLAIN"]);
        let (tf, payload) = fixture("case-sensitive", &data);
        let lower = compile(r#"{"msg":{"values":["plain"],"case":true}}"#);
        assert!(rows(&scan(&payload, tf.file(), "case", &lower).unwrap()).is_empty());
        let exact = compile(r#"{"msg":{"values":["PLAIN"],"case":true}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "case", &exact).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_case_insensitive_ascii() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: Plain ASCII"]);
        let (tf, payload) = fixture("case-insensitive", &data);
        for needle in ["plain", "PLAIN", "PlAiN"] {
            let filter = compile(&format!(r#"{{"msg":{{"values":["{needle}"]}}}}"#));
            assert_eq!(
                rows(&scan(&payload, tf.file(), "case", &filter).unwrap()),
                vec![0],
                "{needle}"
            );
        }
    }

    #[test]
    fn filter_msg_eszett_equivalent() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: Straße",
            "11-14 12:00:01.000  100  200 I Tag: ẞ",
        ]);
        let (tf, payload) = fixture("eszett", &data);
        let filter = compile(r#"{"msg":{"values":["ß"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "eszett", &filter).unwrap()),
            vec![0, 1]
        );
    }

    #[test]
    fn filter_msg_kelvin_sign_folds_to_ascii() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: k"]);
        let (tf, payload) = fixture("kelvin", &data);
        let filter = compile(r#"{"msg":{"values":["\u212A"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "kelvin", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_turkish_dotless_i_not_equal() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: ı"]);
        let (tf, payload) = fixture("dotless-i", &data);
        let filter = compile(r#"{"msg":{"values":["i"]}}"#);
        assert!(rows(&scan(&payload, tf.file(), "dotless", &filter).unwrap()).is_empty());
    }

    #[test]
    fn filter_msg_accent_not_equal() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: CAFÉ",
            "11-14 12:00:01.000  100  200 I Tag: cafe",
        ]);
        let (tf, payload) = fixture("accent", &data);
        let filter = compile(r#"{"msg":{"values":["café"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "accent", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_non_ascii_fold() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: ÄÖÜ"]);
        let (tf, payload) = fixture("non-ascii-fold", &data);
        let filter = compile(r#"{"msg":{"values":["äöü"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "non-ascii", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_lossy_invalid_utf8() {
        let mut data = b"11-14 12:00:00.000  100  200 I Tag: ".to_vec();
        data.push(0xFF);
        data.push(b'\n');
        data.extend_from_slice(b"11-14 12:00:01.000  100  200 I Tag: good\n");
        let (tf, payload) = fixture("invalid-utf8", &data);
        let filter = compile(r#"{"msg":{"values":["\uFFFD"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "invalid-utf8", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_or() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: start",
            "11-14 12:00:01.000  100  200 I Tag: timeout",
            "11-14 12:00:02.000  100  200 I Tag: none",
        ]);
        let (tf, payload) = fixture("msg-or", &data);
        let filter = compile(r#"{"msg":{"values":["start","timeout"],"op":"or"}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "or", &filter).unwrap()),
            vec![0, 1]
        );
    }

    #[test]
    fn filter_msg_and() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: start failed"]);
        let (tf, payload) = fixture("msg-and", &data);
        let filter = compile(r#"{"msg":{"values":["start","failed"],"op":"and"}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "and", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_msg_empty_needle_or_hits_all() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: a",
            "11-14 12:00:01.000  100  200 I Tag: b",
            "11-14 12:00:02.000  100  200 I Tag: c",
        ]);
        let (tf, payload) = fixture("empty-or", &data);
        let filter = compile(r#"{"msg":{"values":["","absent"],"op":"or"}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "empty-or", &filter).unwrap()),
            vec![0, 1, 2]
        );
    }

    #[test]
    fn filter_msg_and_all_empty_hits_all() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: a",
            "11-14 12:00:01.000  100  200 I Tag: b",
            "11-14 12:00:02.000  100  200 I Tag: c",
        ]);
        let (tf, payload) = fixture("all-empty-and", &data);
        let filter = compile(r#"{"msg":{"values":["",""],"op":"and"}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "all-empty", &filter).unwrap()),
            vec![0, 1, 2]
        );
    }

    #[test]
    fn filter_msg_empty_needle_or_exclude_returns_empty() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: a",
            "11-14 12:00:01.000  100  200 I Tag: b",
        ]);
        let (tf, payload) = fixture("empty-or-exclude", &data);
        let filter = compile(r#"{"msg":{"values":["","absent"],"op":"or","exclude":true}}"#);
        assert!(rows(&scan(&payload, tf.file(), "exclude", &filter).unwrap()).is_empty());
    }

    #[test]
    fn filter_msg_all_empty_and_exclude_returns_empty() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: a",
            "11-14 12:00:01.000  100  200 I Tag: b",
        ]);
        let (tf, payload) = fixture("all-empty-and-exclude", &data);
        let filter = compile(r#"{"msg":{"values":["",""],"op":"and","exclude":true}}"#);
        assert!(rows(&scan(&payload, tf.file(), "exclude", &filter).unwrap()).is_empty());
    }

    #[test]
    fn filter_msg_exclude_keeps_nonmatch() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: start",
            "11-14 12:00:01.000  100  200 I Tag: other",
            "11-14 12:00:02.000  100  200 I Tag: ",
        ]);
        let (tf, payload) = fixture("msg-exclude", &data);
        let filter = compile(r#"{"msg":{"values":["start"],"exclude":true}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "exclude", &filter).unwrap()),
            vec![1, 2]
        );
    }

    #[test]
    fn filter_msg_exclude_keeps_empty_body() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: start",
            "11-14 12:00:01.000  100  200 I Tag: ",
        ]);
        let (tf, payload) = fixture("exclude-empty-body", &data);
        let filter = compile(r#"{"msg":{"values":["start"],"exclude":true}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "exclude", &filter).unwrap()),
            vec![1]
        );
    }

    #[test]
    fn filter_text_truncated_returns_io_error() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: needle"]);
        let (tf, payload) = fixture("truncated", &data);
        let cut = body_start(&payload, 0) + 1;
        {
            let file = std::fs::OpenOptions::new()
                .write(true)
                .open(tf.path())
                .unwrap();
            file.set_len(cut).unwrap();
        }
        let filter = compile(r#"{"msg":{"values":["needle"]}}"#);
        let err = scan(&payload, tf.file(), "truncated.log", &filter)
            .err()
            .expect("expected IO error");
        assert_eq!(err.code, IO_ERROR);
    }

    #[test]
    fn filter_text_error_carries_path_and_os_error() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: needle"]);
        let (tf, payload) = fixture("error-shape", &data);
        let cut = body_start(&payload, 0) + 1;
        {
            let file = std::fs::OpenOptions::new()
                .write(true)
                .open(tf.path())
                .unwrap();
            file.set_len(cut).unwrap();
        }
        let filter = compile(r#"{"msg":{"values":["needle"]}}"#);
        let err = scan(&payload, tf.file(), "shape.log", &filter)
            .err()
            .expect("expected IO error");
        assert_eq!(err.code, IO_ERROR);
        assert_eq!(
            err.params.get("path").and_then(|value| value.as_str()),
            Some("shape.log")
        );
        assert!(err.params.get("osError").is_some());
    }

    #[test]
    fn filter_after_rename_reads_loaded_content() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: old"]);
        let (tf, payload) = fixture("rename", &data);
        let old = compile(r#"{"msg":{"values":["old"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "rename", &old).unwrap()),
            vec![0]
        );
        let renamed = tf.dir().join("b.log");
        std::fs::rename(tf.path(), &renamed).unwrap();
        std::fs::write(
            tf.path(),
            lines_data(&["11-14 12:00:00.000  100  200 I Tag: new"]),
        )
        .unwrap();
        assert_eq!(
            rows(&scan(&payload, tf.file(), "rename", &old).unwrap()),
            vec![0]
        );
        let new = compile(r#"{"msg":{"values":["new"]}}"#);
        assert!(rows(&scan(&payload, tf.file(), "rename", &new).unwrap()).is_empty());
    }

    #[test]
    fn filter_after_replacement_reads_loaded_content() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: old"]);
        let (tf, payload) = fixture("replacement", &data);
        let old = compile(r#"{"msg":{"values":["old"]}}"#);
        let replacement = tf.dir().join("replacement.tmp");
        std::fs::write(
            &replacement,
            lines_data(&["11-14 12:00:00.000  100  200 I Tag: new"]),
        )
        .unwrap();
        std::fs::rename(&replacement, tf.path()).unwrap();
        assert_eq!(
            rows(&scan(&payload, tf.file(), "replacement", &old).unwrap()),
            vec![0]
        );
        let new = compile(r#"{"msg":{"values":["new"]}}"#);
        assert!(rows(&scan(&payload, tf.file(), "replacement", &new).unwrap()).is_empty());
    }

    #[test]
    fn filter_after_deletion_reads_loaded_content() {
        let data = lines_data(&["11-14 12:00:00.000  100  200 I Tag: old"]);
        let (tf, payload) = fixture("deletion", &data);
        std::fs::remove_file(tf.path()).unwrap();
        let filter = compile(r#"{"msg":{"values":["old"]}}"#);
        assert_eq!(
            rows(&scan(&payload, tf.file(), "deletion", &filter).unwrap()),
            vec![0]
        );
    }

    #[test]
    fn filter_result_page_anchor_exact_hit() {
        let (_tf, _data, payload) = ten_rows("anchor-exact");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let ord = set.resolve(5).expect("anchor hit") as usize;
        let page = page_rows_for(&set, &payload, ord, 2);
        assert_eq!(page.len(), 2);
        assert_eq!(page[0].doc_row, 5);
    }

    #[test]
    fn filter_result_page_anchor_nearest() {
        let (_tf, _data, payload) = ten_rows("anchor-nearest");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let ord = set.resolve(7).expect("nearest") as usize;
        let page = page_rows_for(&set, &payload, ord, 2);
        assert_eq!(page[0].doc_row, 5);
    }

    #[test]
    fn filter_result_page_anchor_past_end_takes_last() {
        let (_tf, _data, payload) = ten_rows("anchor-past-end");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let ord = set.resolve(10).expect("last") as usize;
        let page = page_rows_for(&set, &payload, ord, 2);
        assert_eq!(page.len(), 1);
        assert_eq!(page[0].doc_row, 9);
    }

    #[test]
    fn filter_result_page_empty_result_empty() {
        let (_tf, _data, payload) = ten_rows("anchor-empty");
        let set = ResultSet::from_rows(Vec::new(), 10);
        assert!(set.resolve(3).is_none());
        assert!(page_rows_for(&set, &payload, 0, 2).is_empty());
    }

    #[test]
    fn filter_result_page_forward_no_repeat() {
        let (_tf, _data, payload) = ten_rows("forward-no-repeat");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let page = forward_page(&set, &payload, 6, 2);
        assert_eq!(page[0].doc_row, 9, "从锚点之后开始，不回退到 5");
    }

    #[test]
    fn filter_result_page_forward_past_end_empty() {
        let (_tf, _data, payload) = ten_rows("forward-past-end");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        assert!(forward_page(&set, &payload, 10, 2).is_empty());
    }

    #[test]
    fn filter_result_page_tail_short() {
        let (_tf, _data, payload) = ten_rows("tail-short");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let page = page_rows_for(&set, &payload, 1, 16);
        assert_eq!(
            page.iter().map(|row| row.doc_row).collect::<Vec<_>>(),
            vec![5, 9]
        );
    }

    #[test]
    fn filter_result_page_text_matches_source() {
        let (tf, data, payload) = ten_rows("page-text");
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        let page = page_rows_for(&set, &payload, 0, 3);
        for row in page {
            let index = row.doc_row as usize;
            let offset = payload.offs[index].max(0) as u64;
            let len = payload.lens[index].max(0) as usize;
            let mut text = vec![0u8; len];
            crate::identity::read_exact_at(tf.file(), &mut text, offset).unwrap();
            assert_eq!(text, data[offset as usize..offset as usize + len]);
            assert!(text.ends_with(format!("line {index}").as_bytes()));
        }
    }

    #[test]
    fn filter_result_resolve_exact_hit() {
        let set = ResultSet::from_rows(vec![3, 7, 8], 10);
        assert_eq!(set.resolve(7), Some(1));
    }

    #[test]
    fn filter_result_resolve_nearest_tie_previous() {
        let set = ResultSet::from_rows(vec![0, 2, 5], 10);
        assert_eq!(set.resolve(1), Some(0));
    }

    #[test]
    fn filter_result_resolve_clamps_ends() {
        let set = ResultSet::from_rows(vec![2, 5], 10);
        assert_eq!(set.resolve(0), Some(0));
        assert_eq!(set.resolve(9), Some(1));
    }

    #[test]
    fn filter_result_resolve_empty_none() {
        let set = ResultSet::from_rows(Vec::new(), 10);
        assert_eq!(set.resolve(3), None);
    }

    #[test]
    fn filter_repeat_returns_same_rows() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: hit",
            "11-14 12:00:01.000  100  200 I Tag: miss",
        ]);
        let (tf, payload) = fixture("repeat", &data);
        let filter = compile(r#"{"msg":{"values":["hit"]}}"#);
        let first = scan(&payload, tf.file(), "repeat", &filter).unwrap();
        let second = scan(&payload, tf.file(), "repeat", &filter).unwrap();
        assert_eq!(first, second);
    }

    #[test]
    fn filter_does_not_mutate_payload() {
        let data = lines_data(&[
            "11-14 12:00:00.000  100  200 I Tag: hit",
            "11-14 12:00:01.000  100  200 I Tag: miss",
        ]);
        let (tf, payload) = fixture("payload", &data);
        let before = payload.clone();
        let filter = compile(r#"{"msg":{"values":["hit"]}}"#);
        let _ = scan(&payload, tf.file(), "payload", &filter).unwrap();
        assert_eq!(payload, before);
    }

    #[test]
    fn filter_page_bytes_unchanged_after_filter() {
        let (tf, _data, payload) = ten_rows("page-bytes");
        let set = ResultSet::identity(10);
        let before = page_bytes(&set, &payload, tf.file(), 0, 4);
        let filter = compile(r#"{"msg":{"values":["line 2"]}}"#);
        let _ = scan(&payload, tf.file(), "page-bytes", &filter).unwrap();
        let after = page_bytes(&set, &payload, tf.file(), 0, 4);
        assert_eq!(before, after);
    }
}