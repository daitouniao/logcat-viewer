//! 页行装配（D2/D3/D5）：结果集序号（`ord`）映射到文档行号（`doc_row`）后取数。
//!
//! 页读取以装载完成后的整份 `Arc<Payload>` 为唯一读面，不读段列表；
//! 行记录中的行号一律是文档行号，结果集序号只作页内坐标。

use std::collections::HashMap;

use crate::result::ResultSet;
use crate::sidecar::{CrashEntry, Payload};

pub const MAGIC: [u8; 4] = *b"LPAG";
pub const VERSION: u32 = 1;
pub const HEADER_BYTES: usize = 36;
pub const ROW_RECORD_BYTES: usize = 52;
pub const MAX_PAGE_ROWS: usize = 4096;
const NO_REF: u32 = u32::MAX;

/// 页行中间结构：列值 + 展开后的字典字符串（`None` 表示空）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PageRow {
    pub doc_row: u32,
    pub level: u8,
    pub pid: i32,
    pub tid: i32,
    pub moff: u32,
    pub tag: Option<String>,
    pub crash_kind: Option<u8>,
    pub exc: Option<String>,
    pub pkg: Option<String>,
}

/// 低层原语：取结果集 `[ord_start, ord_start + count)` 的连续一段。
/// 越界截短；`ord_start` 越界或 `count == 0` 返回空。
pub fn page_rows_for(
    result: &ResultSet,
    payload: &Payload,
    ord_start: usize,
    count: usize,
) -> Vec<PageRow> {
    let len = result.len();
    if ord_start >= len || count == 0 {
        return Vec::new();
    }
    let end = ord_start.saturating_add(count).min(len);
    let doc_rows: Vec<u32> = (ord_start..end).map(|ord| result.doc_row(ord)).collect();
    rows_for_doc_rows(&doc_rows, payload)
}

/// 内核：按文档行号装配行内容；`doc_rows` 必须升序且小于文档行数。
fn rows_for_doc_rows(doc_rows: &[u32], payload: &Payload) -> Vec<PageRow> {
    debug_assert!(doc_rows.windows(2).all(|w| w[0] < w[1]), "doc_rows 必须升序");
    let crash_by_row = crash_rows_for(doc_rows, payload);
    doc_rows
        .iter()
        .map(|&doc_row| {
            let i = doc_row as usize;
            let crash = crash_by_row.get(&(doc_row as i32)).copied();
            PageRow {
                doc_row,
                level: payload.lvls[i],
                pid: payload.pids[i],
                tid: payload.tids[i],
                moff: payload.moff[i].max(0) as u32,
                tag: dict_get(&payload.tags, payload.tag_ids[i]),
                crash_kind: crash.map(|c| c.kind),
                exc: crash.and_then(|c| dict_get(&payload.exc_types, c.exc_id)),
                pkg: crash.and_then(|c| dict_get(&payload.pkgs, c.pkg_id)),
            }
        })
        .collect()
}

fn crash_rows_for<'a>(doc_rows: &[u32], payload: &'a Payload) -> HashMap<i32, &'a CrashEntry> {
    payload
        .crashes
        .iter()
        .filter(|c| c.row >= 0 && doc_rows.binary_search(&(c.row as u32)).is_ok())
        .map(|c| (c.row, c))
        .collect()
}

fn dict_get(dict: &[String], id: i32) -> Option<String> {
    if id < 0 {
        None
    } else {
        dict.get(id as usize).cloned()
    }
}

/// 带原文的页行：布局编码的输入。原文不含行尾换行。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PageEntry {
    pub row: PageRow,
    pub text: Vec<u8>,
    pub time_start: u32,
    pub time_len: u32,
}

impl PageRow {
    /// 绑定原文并计算时间戳区间（D3：区间只由行首语法决定）。
    pub fn with_text(self, text: Vec<u8>) -> PageEntry {
        let (time_start, time_len) = crate::parse::time_range(&text);
        PageEntry {
            row: self,
            text,
            time_start,
            time_len,
        }
    }
}

/// 编码布局 v1（小端）；空页只含 36 字节头部。
/// 页头第 4 字段写本页首行 `ord`；空页写 0，维持既有字节行为。
pub fn encode_page(entries: &[PageEntry], ord_start: u32) -> Vec<u8> {
    let mut tags = StringTable::default();
    let mut excs = StringTable::default();
    let mut pkgs = StringTable::default();
    let refs: Vec<(u32, u32, u32)> = entries
        .iter()
        .map(|e| {
            (
                tags.intern(e.row.tag.as_deref()),
                excs.intern(e.row.exc.as_deref()),
                pkgs.intern(e.row.pkg.as_deref()),
            )
        })
        .collect();
    let text_bytes: usize = entries.iter().map(|e| e.text.len()).sum();
    let string_bytes: usize = [&tags, &excs, &pkgs]
        .iter()
        .flat_map(|t| t.values.iter())
        .map(|v| 4 + v.len())
        .sum();

    let mut out = Vec::with_capacity(
        HEADER_BYTES + entries.len() * ROW_RECORD_BYTES + string_bytes + text_bytes,
    );
    put_u32(&mut out, u32::from_le_bytes(MAGIC));
    put_u32(&mut out, VERSION);
    put_u32(&mut out, entries.len() as u32);
    put_u32(&mut out, if entries.is_empty() { 0 } else { ord_start });
    put_u32(&mut out, tags.count());
    put_u32(&mut out, excs.count());
    put_u32(&mut out, pkgs.count());
    put_u64(&mut out, text_bytes as u64);

    let mut text_off = 0u64;
    for (entry, (tag_ref, exc_ref, pkg_ref)) in entries.iter().zip(&refs) {
        put_u32(&mut out, entry.row.doc_row);
        out.push(entry.row.level);
        out.push(u8::from(entry.row.crash_kind.is_some()));
        out.push(entry.row.crash_kind.unwrap_or(0));
        out.push(0);
        put_i32(&mut out, entry.row.pid);
        put_i32(&mut out, entry.row.tid);
        put_u32(&mut out, entry.row.moff);
        put_u32(&mut out, entry.time_start);
        put_u32(&mut out, entry.time_len);
        put_u64(&mut out, text_off);
        put_u32(&mut out, entry.text.len() as u32);
        put_u32(&mut out, *tag_ref);
        put_u32(&mut out, *exc_ref);
        put_u32(&mut out, *pkg_ref);
        text_off += entry.text.len() as u64;
    }
    tags.write(&mut out);
    excs.write(&mut out);
    pkgs.write(&mut out);
    for entry in entries {
        out.extend_from_slice(&entry.text);
    }
    debug_assert_eq!(out.len(), HEADER_BYTES + entries.len() * ROW_RECORD_BYTES + string_bytes + text_bytes);
    out
}

#[derive(Default)]
struct StringTable {
    values: Vec<String>,
    index: HashMap<String, u32>,
}

impl StringTable {
    fn intern(&mut self, value: Option<&str>) -> u32 {
        let Some(v) = value else {
            return NO_REF;
        };
        match self.index.get(v) {
            Some(id) => *id,
            None => {
                let id = self.values.len() as u32;
                self.values.push(v.to_string());
                self.index.insert(v.to_string(), id);
                id
            }
        }
    }

    fn count(&self) -> u32 {
        self.values.len() as u32
    }

    fn write(&self, out: &mut Vec<u8>) {
        for v in &self.values {
            put_u32(out, v.len() as u32);
            out.extend_from_slice(v.as_bytes());
        }
    }
}

fn put_u32(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn put_i32(out: &mut Vec<u8>, v: i32) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn put_u64(out: &mut Vec<u8>, v: u64) {
    out.extend_from_slice(&v.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::parse::parse_bytes;

    fn sample() -> Vec<u8> {
        let mut data = Vec::new();
        for i in 0..10 {
            data.extend_from_slice(
                format!("11-14 12:00:{:02}.123  10  20 I Tag{}: line {i}\n", i, i % 2)
                    .as_bytes(),
            );
        }
        data
    }

    fn crash_sample() -> &'static [u8] {
        b"11-14 12:00:00.000  100  200 E AndroidRuntime: FATAL EXCEPTION: main\n\
11-14 12:00:00.001  100  200 E AndroidRuntime: Process: com.example.app, PID: 100\n\
11-14 12:00:00.002  100  200 E AndroidRuntime: java.lang.RuntimeException: boom\n\
11-14 12:00:00.003  100  200 I T: normal 1\n\
11-14 12:00:00.004  100  200 E AndroidRuntime: FATAL EXCEPTION: second\n\
11-14 12:00:00.005  100  200 E AndroidRuntime: Process: com.example.other, PID: 100\n\
11-14 12:00:00.006  100  200 E AndroidRuntime: not an exception\n\
11-14 12:00:00.007  100  200 I T: normal 2\n"
    }

    fn bind(payload: &Payload, rows: Vec<PageRow>, data: &[u8]) -> Vec<PageEntry> {
        rows.into_iter()
            .map(|row| {
                let i = row.doc_row as usize;
                let off = payload.offs[i].max(0) as usize;
                let len = payload.lens[i].max(0) as usize;
                row.with_text(data[off..off + len].to_vec())
            })
            .collect()
    }

    #[test]
    fn page_rows_for_identity_contiguous() {
        let payload = parse_bytes(&sample());
        let set = ResultSet::identity(payload.row_count() as u32);
        let rows = page_rows_for(&set, &payload, 2, 4);
        assert_eq!(rows.len(), 4);
        for (k, row) in rows.iter().enumerate() {
            let i = 2 + k;
            assert_eq!(row.doc_row, i as u32, "文档行号连续");
            assert_eq!(row.level, 3, "I 级别");
            assert_eq!((row.pid, row.tid), (10, 20));
            assert_eq!(row.tag.as_deref(), Some(format!("Tag{}", i % 2).as_str()));
            assert_eq!(row.crash_kind, None);
            assert_eq!((&row.exc, &row.pkg), (&None, &None));
        }
    }

    #[test]
    fn page_rows_for_start_beyond_empty() {
        let payload = parse_bytes(&sample());
        let set = ResultSet::identity(payload.row_count() as u32);
        assert!(page_rows_for(&set, &payload, payload.row_count(), 10).is_empty());
        assert!(page_rows_for(&set, &payload, payload.row_count() + 5, 10).is_empty());
    }

    #[test]
    fn page_rows_for_tail_short_page() {
        let payload = parse_bytes(&sample());
        let set = ResultSet::identity(payload.row_count() as u32);
        let rows = page_rows_for(&set, &payload, payload.row_count() - 1, 16);
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].doc_row as usize, payload.row_count() - 1);
    }

    #[test]
    fn page_binary_header_fixed() {
        let buf = encode_page(&[], 0);
        let expected: [u8; HEADER_BYTES] = [
            b'L', b'P', b'A', b'G', // magic
            1, 0, 0, 0, // version
            0, 0, 0, 0, // rows
            0, 0, 0, 0, // startOrd
            0, 0, 0, 0, // tagCount
            0, 0, 0, 0, // excCount
            0, 0, 0, 0, // pkgCount
            0, 0, 0, 0, 0, 0, 0, 0, // textBytes
        ];
        assert_eq!(buf, expected);
    }

    #[test]
    fn page_binary_row_record_fixed() {
        let entry = PageEntry {
            row: PageRow {
                doc_row: 7,
                level: 5,
                pid: 100,
                tid: 200,
                moff: 21,
                tag: Some("T".into()),
                crash_kind: Some(0),
                exc: None,
                pkg: None,
            },
            text: b"abc".to_vec(),
            time_start: 2,
            time_len: 14,
        };
        let buf = encode_page(&[entry], 0);
        assert_eq!(&buf[..4], b"LPAG");
        assert_eq!(buf.len(), HEADER_BYTES + ROW_RECORD_BYTES + (4 + 1) + 3);
        let expected: [u8; ROW_RECORD_BYTES] = [
            7, 0, 0, 0, // docRow
            5, // level
            1, // flags：崩溃行
            0, // crashKind：Java
            0, // pad
            100, 0, 0, 0, // pid
            200, 0, 0, 0, // tid
            21, 0, 0, 0, // moff
            2, 0, 0, 0, // timeStart
            14, 0, 0, 0, // timeLen
            0, 0, 0, 0, 0, 0, 0, 0, // textOff
            3, 0, 0, 0, // textLen
            0, 0, 0, 0, // tagRef
            255, 255, 255, 255, // excRef
            255, 255, 255, 255, // pkgRef
        ];
        assert_eq!(&buf[HEADER_BYTES..HEADER_BYTES + ROW_RECORD_BYTES], expected);
        let tail = HEADER_BYTES + ROW_RECORD_BYTES;
        assert_eq!(&buf[tail..tail + 5], [1, 0, 0, 0, b'T']);
        assert_eq!(&buf[tail + 5..], b"abc");
    }

    #[test]
    fn page_binary_header_start_ord() {
        let data = sample();
        let payload = parse_bytes(&data);
        let set = ResultSet::from_rows(vec![2, 5], 10);
        let rows = page_rows_for(&set, &payload, 0, 2);
        let entries = bind(&payload, rows, &data);
        let buf = encode_page(&entries, 1);
        assert_eq!(u32::from_le_bytes(buf[12..16].try_into().unwrap()), 1, "页头写首行 ord");
        let first = u32::from_le_bytes(buf[HEADER_BYTES..HEADER_BYTES + 4].try_into().unwrap());
        let second_at = HEADER_BYTES + ROW_RECORD_BYTES;
        let second = u32::from_le_bytes(buf[second_at..second_at + 4].try_into().unwrap());
        assert_eq!((first, second), (2, 5), "行记录写 docRow");
    }

    #[test]
    fn page_binary_string_tables_dedup() {
        let make = |doc_row: u32, tag: &str| PageEntry {
            row: PageRow {
                doc_row,
                level: 3,
                pid: 1,
                tid: 1,
                moff: 0,
                tag: Some(tag.into()),
                crash_kind: None,
                exc: None,
                pkg: None,
            },
            text: Vec::new(),
            time_start: 0,
            time_len: 0,
        };
        let buf = encode_page(&[make(0, "T"), make(1, "T"), make(2, "U")], 0);
        let tag_count = u32::from_le_bytes(buf[16..20].try_into().unwrap());
        assert_eq!(tag_count, 2, "页内去重");
        let refs: Vec<u32> = (0..3)
            .map(|i| {
                let at = HEADER_BYTES + i * ROW_RECORD_BYTES + 40;
                u32::from_le_bytes(buf[at..at + 4].try_into().unwrap())
            })
            .collect();
        assert_eq!(refs, vec![0, 0, 1]);
        let tables = HEADER_BYTES + 3 * ROW_RECORD_BYTES;
        assert_eq!(&buf[tables..tables + 10], [1, 0, 0, 0, b'T', 1, 0, 0, 0, b'U']);
    }

    #[test]
    fn page_binary_text_blob_concatenated() {
        let make = |doc_row: u32, text: &[u8]| PageEntry {
            row: PageRow {
                doc_row,
                level: 3,
                pid: 1,
                tid: 1,
                moff: 0,
                tag: None,
                crash_kind: None,
                exc: None,
                pkg: None,
            },
            text: text.to_vec(),
            time_start: 0,
            time_len: 0,
        };
        let buf = encode_page(&[make(0, b"abc"), make(1, b"de")], 0);
        let text_bytes = u64::from_le_bytes(buf[28..36].try_into().unwrap());
        assert_eq!(text_bytes, 5);
        let first_off = u64::from_le_bytes(
            buf[HEADER_BYTES + 28..HEADER_BYTES + 36].try_into().unwrap(),
        );
        let second = HEADER_BYTES + ROW_RECORD_BYTES;
        let second_off = u64::from_le_bytes(buf[second + 28..second + 36].try_into().unwrap());
        assert_eq!((first_off, second_off), (0, 3));
        let first_len = u32::from_le_bytes(buf[HEADER_BYTES + 36..HEADER_BYTES + 40].try_into().unwrap());
        let second_len = u32::from_le_bytes(buf[second + 36..second + 40].try_into().unwrap());
        assert_eq!((first_len, second_len), (3, 2));
        assert_eq!(&buf[buf.len() - 5..], b"abcde");
    }

    #[test]
    fn page_rows_for_identity_crash_fields() {
        let payload = parse_bytes(crash_sample());
        let set = ResultSet::identity(payload.row_count() as u32);
        let rows = page_rows_for(&set, &payload, 0, 8);
        assert_eq!(rows.len(), 8);

        assert_eq!(rows[0].crash_kind, Some(crate::crash::KIND_JAVA));
        assert_eq!(rows[0].exc.as_deref(), Some("java.lang.RuntimeException"));
        assert_eq!(rows[0].pkg.as_deref(), Some("com.example.app"));

        assert_eq!(rows[4].crash_kind, Some(crate::crash::KIND_JAVA));
        assert_eq!(rows[4].exc, None, "异常类型不在已知命名空间时为空");
        assert_eq!(rows[4].pkg.as_deref(), Some("com.example.other"));

        for i in [1, 2, 3, 5, 6, 7] {
            assert_eq!(rows[i].crash_kind, None, "非崩溃头不带崩溃字段（行 {i}）");
            assert_eq!((&rows[i].exc, &rows[i].pkg), (&None, &None));
        }
    }

    #[test]
    fn page_rows_for_result_uses_doc_row_for_text() {
        let data = sample();
        let payload = parse_bytes(&data);
        let set = ResultSet::from_rows(vec![0, 2, 5], 10);
        let rows = page_rows_for(&set, &payload, 0, 3);
        assert_eq!(
            rows.iter().map(|r| r.doc_row).collect::<Vec<_>>(),
            vec![0, 2, 5]
        );
        let entries = bind(&payload, rows, &data);
        for entry in &entries {
            let i = entry.row.doc_row as usize;
            let text = String::from_utf8_lossy(&entry.text);
            assert!(text.ends_with(&format!("line {i}")), "原文按 docRow 取：{text}");
            assert_eq!(
                entry.row.tag.as_deref(),
                Some(format!("Tag{}", i % 2).as_str())
            );
            let moff = payload.moff[i].max(0) as usize;
            assert_eq!(entry.row.moff, moff as u32);
            assert!(entry.text[moff..].starts_with(b"line "), "moff 按 docRow 取");
            assert_eq!((entry.time_start, entry.time_len), (0, 18));
        }
    }

    #[test]
    fn page_rows_for_result_crash_fields_follow_doc_row() {
        let payload = parse_bytes(crash_sample());
        let set = ResultSet::from_rows(vec![0, 4, 7], 8);
        let rows = page_rows_for(&set, &payload, 0, 3);
        assert_eq!(
            rows.iter().map(|r| r.doc_row).collect::<Vec<_>>(),
            vec![0, 4, 7]
        );

        assert_eq!(rows[0].crash_kind, Some(crate::crash::KIND_JAVA));
        assert_eq!(rows[0].exc.as_deref(), Some("java.lang.RuntimeException"));
        assert_eq!(rows[0].pkg.as_deref(), Some("com.example.app"));

        assert_eq!(rows[1].crash_kind, Some(crate::crash::KIND_JAVA));
        assert_eq!(rows[1].exc, None);
        assert_eq!(rows[1].pkg.as_deref(), Some("com.example.other"));

        assert_eq!(rows[2].crash_kind, None);
        assert_eq!((&rows[2].exc, &rows[2].pkg), (&None, &None));
    }
}