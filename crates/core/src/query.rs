//! 查询句柄：在已装载文件源上按过滤表达式开启，持有开启时刻的装载快照与结果集。
//!
//! 查询与源句柄解耦：源关闭后查询继续读开启时的装载内容（`Arc` 零复制共享索引）。
//! 对外坐标一律为真实文档行号，结果集序号不越出本模块。

use std::fs::File;
use std::sync::Arc;

use crate::error::{CoreError, CoreResult};
use crate::filter::{self, FilterExpr};
use crate::page::{self, encode_page, MAX_PAGE_ROWS};
use crate::result::ResultSet;
use crate::sidecar::Payload;
use crate::source::SourceHandle;

pub struct QueryHandle {
    payload: Arc<Payload>,
    file: Arc<File>,
    display: String,
    result: ResultSet,
}

/// 开启查询：状态门禁先于过滤条件校验；扫描在调用线程同步完成。
pub fn open(source: &SourceHandle, filter_json: &str) -> CoreResult<QueryHandle> {
    let (payload, file, display) = source.query_snapshot()?;
    let compiled = FilterExpr::from_json(filter_json)?.compile()?;
    let result = filter::scan(&payload, &file, &display, &compiled)?;
    Ok(QueryHandle {
        payload,
        file,
        display,
        result,
    })
}

impl QueryHandle {
    pub fn row_count(&self) -> u64 {
        self.result.len() as u64
    }

    /// 定位最近结果行并返回其**文档行号**；空结果集返回 `None`，负锚点取首条。
    pub fn resolve(&self, doc_row: i64) -> Option<u32> {
        if self.result.is_empty() {
            return None;
        }
        let ord = if doc_row < 0 {
            0
        } else {
            self.result.resolve(doc_row as u32)?
        };
        Some(self.result.doc_row(ord as usize))
    }

    /// 向后取页：`start` 为真实文档行号，页首取文档行号不小于 `start` 的第一条结果。
    /// 负起点先于条数归因；条数为零或起点越界返回空页。
    pub fn page(&self, start: i64, count: i64) -> CoreResult<Vec<u8>> {
        if start < 0 {
            return Err(CoreError::invalid_arg("start"));
        }
        if count < 0 || count as usize > MAX_PAGE_ROWS {
            return Err(CoreError::invalid_arg("count"));
        }
        if count == 0 {
            return Ok(encode_page(&[], 0));
        }
        let Ok(start) = u32::try_from(start) else {
            return Ok(encode_page(&[], 0));
        };
        let Some(ord) = self.result.forward_ord(start) else {
            return Ok(encode_page(&[], 0));
        };
        page::read_page_bytes(
            &self.result,
            &self.payload,
            &self.file,
            &self.display,
            ord,
            count as usize,
        )
    }

    /// 向上取页：`end` 为排他终点（真实文档行号），取终点前最近 `count` 条，页内升序。
    /// 不足 `count` 时返回全部；终点之前无结果返回空页。
    pub fn page_backward(&self, end: i64, count: i64) -> CoreResult<Vec<u8>> {
        if end < 0 {
            return Err(CoreError::invalid_arg("end"));
        }
        if count < 0 || count as usize > MAX_PAGE_ROWS {
            return Err(CoreError::invalid_arg("count"));
        }
        if count == 0 {
            return Ok(encode_page(&[], 0));
        }
        let before = if end > u32::MAX as i64 {
            self.result.len()
        } else {
            self.result.count_before(end as u32)
        };
        if before == 0 {
            return Ok(encode_page(&[], 0));
        }
        let start = before.saturating_sub(count as usize);
        page::read_page_bytes(
            &self.result,
            &self.payload,
            &self.file,
            &self.display,
            start,
            (count as usize).min(before),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{CLOSED, INVALID_ARG, INVALID_STATE};
    use crate::state::State;
    use crate::testutil::{file_request, temp_dir, text_file, unique_pipe, wait_until};
    use std::path::PathBuf;

    struct Fixture {
        dir: PathBuf,
        source: Box<SourceHandle>,
    }

    impl Fixture {
        fn new(tag: &str, lines: &[&str]) -> Self {
            let dir = temp_dir(tag);
            let file = text_file(&dir, "q.log", lines);
            let req = file_request(&file, &dir, &dir, &unique_pipe(tag), "", &[], 500, "Auto");
            let source = SourceHandle::plan(&req).unwrap();
            source.commit().unwrap();
            assert!(wait_until(|| source.state() == State::Loaded));
            Self { dir, source }
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    fn lines() -> [&'static str; 3] {
        [
            "11-14 12:00:01.123  100  200 I Tag: one",
            "11-14 12:00:02.123  100  200 I Tag: two",
            "11-14 12:00:03.123  200  300 D Tag: three",
        ]
    }

    fn ten_lines() -> [&'static str; 10] {
        [
            "11-14 12:00:00.000  100  200 I Tag: line 0",
            "11-14 12:00:01.000  100  200 I Tag: line 1",
            "11-14 12:00:02.000  100  200 I Tag: line 2",
            "11-14 12:00:03.000  100  200 I Tag: line 3",
            "11-14 12:00:04.000  100  200 I Tag: line 4",
            "11-14 12:00:05.000  100  200 I Tag: line 5",
            "11-14 12:00:06.000  100  200 I Tag: line 6",
            "11-14 12:00:07.000  100  200 I Tag: line 7",
            "11-14 12:00:08.000  100  200 I Tag: line 8",
            "11-14 12:00:09.000  100  200 I Tag: line 9",
        ]
    }

    fn handle_with_result(f: &Fixture, doc_rows: Vec<u32>) -> QueryHandle {
        let (payload, file, display) = f.source.query_snapshot().unwrap();
        let doc_count = payload.row_count() as u32;
        QueryHandle {
            payload,
            file,
            display,
            result: ResultSet::from_rows(doc_rows, doc_count),
        }
    }

    fn assert_err(result: CoreResult<QueryHandle>, code: i32, field: &str) {
        let err = result.err().expect("期望失败");
        assert_eq!(err.code, code);
        assert_eq!(
            err.params.get("field").and_then(|value| value.as_str()),
            Some(field)
        );
    }

    fn assert_page_err(result: CoreResult<Vec<u8>>, field: &str) {
        let err = result.err().expect("期望失败");
        assert_eq!(err.code, INVALID_ARG);
        assert_eq!(
            err.params.get("field").and_then(|value| value.as_str()),
            Some(field)
        );
    }

    fn page_rows_of(buf: &[u8]) -> Vec<u32> {
        let rows = u32::from_le_bytes(buf[8..12].try_into().unwrap()) as usize;
        (0..rows)
            .map(|k| {
                let at = crate::page::HEADER_BYTES + k * crate::page::ROW_RECORD_BYTES;
                u32::from_le_bytes(buf[at..at + 4].try_into().unwrap())
            })
            .collect()
    }

    fn page_text_of(buf: &[u8]) -> Vec<u8> {
        let text_bytes = u64::from_le_bytes(buf[28..36].try_into().unwrap()) as usize;
        buf[buf.len() - text_bytes..].to_vec()
    }

    #[test]
    fn query_open_empty_filter_is_identity() {
        let f = Fixture::new("q-identity", &lines());
        let query = open(&f.source, "{}").unwrap();
        assert_eq!(query.result, ResultSet::identity(3));
        assert_eq!(query.row_count(), 3);
    }

    #[test]
    fn query_open_filtered_row_count() {
        let f = Fixture::new("q-filtered", &lines());
        let query = open(&f.source, r#"{"pids":{"values":[100]}}"#).unwrap();
        assert_eq!(query.row_count(), 2);
    }

    #[test]
    fn query_open_invalid_filter_reports_field() {
        let f = Fixture::new("q-invalid", &lines());
        assert_err(open(&f.source, r#"{"levels":[0]}"#), INVALID_ARG, "levels");
        assert_err(open(&f.source, "not json"), INVALID_ARG, "filter");
    }

    #[test]
    fn query_open_requires_loaded() {
        let dir = temp_dir("q-not-loaded");
        let file = text_file(&dir, "q.log", &lines());
        let req = file_request(
            &file,
            &dir,
            &dir,
            &unique_pipe("q-not-loaded"),
            "",
            &[],
            500,
            "Auto",
        );
        let source = SourceHandle::plan(&req).unwrap();
        let err = open(&source, "{}").err().expect("期望失败");
        assert_eq!(err.code, INVALID_STATE);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn query_open_after_close_returns_closed() {
        let f = Fixture::new("q-closed", &lines());
        f.source.close().unwrap();
        let err = open(&f.source, "{}").err().expect("期望失败");
        assert_eq!(err.code, CLOSED);
    }

    #[test]
    fn query_resolve_exact_hit() {
        let f = Fixture::new("q-resolve-hit", &ten_lines());
        let query = handle_with_result(&f, vec![3, 7, 8]);
        assert_eq!(query.resolve(7), Some(7), "命中返回自身文档行号");
    }

    #[test]
    fn query_resolve_nearest_tie_previous() {
        let f = Fixture::new("q-resolve-tie", &ten_lines());
        let query = handle_with_result(&f, vec![0, 2, 5]);
        assert_eq!(query.resolve(1), Some(0), "距离相等取前一条");
    }

    #[test]
    fn query_resolve_clamps_ends() {
        let f = Fixture::new("q-resolve-ends", &ten_lines());
        let query = handle_with_result(&f, vec![2, 5]);
        assert_eq!(query.resolve(0), Some(2), "小于首条取首条");
        assert_eq!(query.resolve(9), Some(5), "大于末条取末条");
    }

    #[test]
    fn query_resolve_negative_clamps_first() {
        let f = Fixture::new("q-resolve-negative", &ten_lines());
        let query = handle_with_result(&f, vec![2, 5]);
        assert_eq!(query.resolve(-1), Some(2), "负锚点按小于首条处理");
    }

    #[test]
    fn query_resolve_empty_none() {
        let f = Fixture::new("q-resolve-empty", &ten_lines());
        let query = handle_with_result(&f, Vec::new());
        assert_eq!(query.resolve(3), None);
        assert_eq!(query.resolve(-1), None);
    }

    #[test]
    fn query_page_starts_at_real_doc_row() {
        let f = Fixture::new("q-page-start", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(page_rows_of(&query.page(3, 2).unwrap()), vec![3, 4]);
    }

    #[test]
    fn query_page_forward_skips_non_matching() {
        let f = Fixture::new("q-page-skip", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(page_rows_of(&query.page(5, 2).unwrap()), vec![7, 8]);
    }

    #[test]
    fn query_page_forward_no_repeat() {
        let f = Fixture::new("q-page-norepeat", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let first = page_rows_of(&query.page(0, 2).unwrap());
        let next_start = *first.last().unwrap() as i64 + 1;
        let second = page_rows_of(&query.page(next_start, 2).unwrap());
        assert_eq!(first, vec![3, 4]);
        assert_eq!(second, vec![7, 8], "起点取上页末行 +1 时不重复");
    }

    #[test]
    fn query_page_between_results_starts_next() {
        let f = Fixture::new("q-page-between", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(
            page_rows_of(&query.page(6, 1).unwrap()),
            vec![7],
            "落在两条结果之间时从下一条起"
        );
    }

    #[test]
    fn query_page_past_end_returns_empty() {
        let f = Fixture::new("q-page-past", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let buf = query.page(10, 2).unwrap();
        assert!(page_rows_of(&buf).is_empty());
        assert_eq!(buf.len(), crate::page::HEADER_BYTES);
    }

    #[test]
    fn query_page_zero_count_returns_empty() {
        let f = Fixture::new("q-page-zero", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let buf = query.page(3, 0).unwrap();
        assert!(page_rows_of(&buf).is_empty());
        assert_eq!(buf.len(), crate::page::HEADER_BYTES);
    }

    #[test]
    fn query_page_rejects_negative_start() {
        let f = Fixture::new("q-page-neg-start", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page(-1, 2), "start");
    }

    #[test]
    fn query_page_rejects_zero_count_with_negative_start() {
        let f = Fixture::new("q-page-neg-start-zero", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page(-1, 0), "start");
    }

    #[test]
    fn query_page_rejects_negative_count() {
        let f = Fixture::new("q-page-neg-count", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page(3, -1), "count");
    }

    #[test]
    fn query_page_rejects_count_over_max() {
        let f = Fixture::new("q-page-max", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page(3, MAX_PAGE_ROWS as i64 + 1), "count");
    }

    #[test]
    fn query_page_text_matches_source() {
        let f = Fixture::new("q-page-text", &ten_lines());
        let query = handle_with_result(&f, vec![3, 7]);
        let buf = query.page(7, 1).unwrap();
        assert_eq!(page_text_of(&buf), ten_lines()[7].as_bytes());
    }

    #[test]
    fn query_page_entries_carry_doc_row() {
        let f = Fixture::new("q-page-docrow", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(
            page_rows_of(&query.page(0, 4).unwrap()),
            vec![3, 4, 7, 8],
            "行记录写真实文档行号，非结果序号"
        );
    }

    #[test]
    fn query_page_backward_returns_tail() {
        let f = Fixture::new("q-back-tail", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(page_rows_of(&query.page_backward(10, 2).unwrap()), vec![7, 8]);
    }

    #[test]
    fn query_page_backward_contiguous_no_repeat() {
        let f = Fixture::new("q-back-norepeat", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let tail = page_rows_of(&query.page_backward(10, 2).unwrap());
        let prev = page_rows_of(&query.page_backward(tail[0] as i64, 2).unwrap());
        assert_eq!(tail, vec![7, 8]);
        assert_eq!(prev, vec![3, 4], "终点取上页首行（排他）时不重复");
    }

    #[test]
    fn query_page_backward_before_first_empty() {
        let f = Fixture::new("q-back-first", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let buf = query.page_backward(3, 2).unwrap();
        assert!(page_rows_of(&buf).is_empty(), "首条之前无结果");
        assert_eq!(buf.len(), crate::page::HEADER_BYTES);
        assert!(page_rows_of(&query.page_backward(0, 2).unwrap()).is_empty());
    }

    #[test]
    fn query_page_backward_short_returns_all() {
        let f = Fixture::new("q-back-short", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        assert_eq!(
            page_rows_of(&query.page_backward(10, 5).unwrap()),
            vec![3, 4, 7, 8],
            "不足 count 时返回全部"
        );
        assert_eq!(
            page_rows_of(&query.page_backward(7, 5).unwrap()),
            vec![3, 4],
            "不足时只返回终点之前的结果，越不过排他终点"
        );
    }

    #[test]
    fn query_page_backward_zero_count_returns_empty() {
        let f = Fixture::new("q-back-zero", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4, 7, 8]);
        let buf = query.page_backward(10, 0).unwrap();
        assert!(page_rows_of(&buf).is_empty());
        assert_eq!(buf.len(), crate::page::HEADER_BYTES);
    }

    #[test]
    fn query_page_backward_rejects_negative_end() {
        let f = Fixture::new("q-back-neg-end", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page_backward(-1, 2), "end");
    }

    #[test]
    fn query_page_backward_rejects_negative_count() {
        let f = Fixture::new("q-back-neg-count", &ten_lines());
        let query = handle_with_result(&f, vec![3, 4]);
        assert_page_err(query.page_backward(10, -1), "count");
    }

    #[test]
    fn query_page_backward_text_matches_source() {
        let f = Fixture::new("q-back-text", &ten_lines());
        let query = handle_with_result(&f, vec![3, 7, 9]);
        let buf = query.page_backward(10, 1).unwrap();
        assert_eq!(page_text_of(&buf), ten_lines()[9].as_bytes());
    }

    #[test]
    fn query_survives_source_close() {
        let f = Fixture::new("q-survive-close", &ten_lines());
        let query = open(&f.source, r#"{"pids":{"values":[100]}}"#).unwrap();
        assert_eq!(query.row_count(), 10);
        f.source.close().unwrap();
        assert_eq!(query.row_count(), 10, "源关闭后查询仍可读");
        assert_eq!(query.resolve(5), Some(5));
        assert_eq!(page_rows_of(&query.page(0, 2).unwrap()), vec![0, 1]);
    }

    #[test]
    fn query_open_empty_document() {
        let f = Fixture::new("q-empty-doc", &[]);
        let query = open(&f.source, "{}").unwrap();
        assert_eq!(query.row_count(), 0);
        assert_eq!(query.resolve(0), None);
        let buf = query.page(0, 10).unwrap();
        assert!(page_rows_of(&buf).is_empty());
        assert!(page_rows_of(&query.page_backward(0, 10).unwrap()).is_empty());
    }

    #[test]
    fn query_page_truncated_returns_io_error() {
        let f = Fixture::new("q-truncated", &ten_lines());
        let query = open(&f.source, "{}").unwrap();
        let path = f.dir.join("q.log");
        std::fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .unwrap()
            .set_len(0)
            .unwrap();
        let err = query.page(0, 2).unwrap_err();
        assert_eq!(err.code, crate::error::IO_ERROR);
        assert_eq!(
            err.params.get("path").and_then(|v| v.as_str()),
            Some(path.to_string_lossy().as_ref())
        );
        assert!(err.params.contains_key("osError"));
        assert_eq!(query.resolve(5), Some(5), "读取失败不改查询定位");
    }

    #[test]
    fn query_page_after_rename_reads_loaded_content() {
        let f = Fixture::new("q-rename", &ten_lines());
        let query = open(&f.source, "{}").unwrap();
        let path = f.dir.join("q.log");
        std::fs::rename(&path, f.dir.join("moved.log")).unwrap();
        let buf = query.page(0, 1).unwrap();
        assert_eq!(page_rows_of(&buf), vec![0]);
        assert_eq!(page_text_of(&buf), ten_lines()[0].as_bytes());
    }

    #[test]
    fn query_page_after_replacement_reads_loaded_content() {
        let f = Fixture::new("q-replaced", &ten_lines());
        let query = open(&f.source, "{}").unwrap();
        let path = f.dir.join("q.log");
        std::fs::rename(&path, f.dir.join("orig.log")).unwrap();
        std::fs::write(
            &path,
            "11-14 12:00:09.000  999  999 W New: replaced\n",
        )
        .unwrap();
        let buf = query.page(0, 1).unwrap();
        assert_eq!(page_rows_of(&buf), vec![0]);
        assert_eq!(
            page_text_of(&buf),
            ten_lines()[0].as_bytes(),
            "同路径替换后仍读开启时的内容"
        );
    }

    #[test]
    fn query_does_not_change_source_paging() {
        let f = Fixture::new("q-source-page", &ten_lines());
        let before = f.source.page(0, 3).unwrap();
        let query = open(&f.source, r#"{"pids":{"values":[100]}}"#).unwrap();
        let _ = query.page(4, 2).unwrap();
        let after = f.source.page(0, 3).unwrap();
        assert_eq!(before, after, "查询取页不改变源分页输出");
        assert_eq!(f.source.row_count().unwrap(), 10);
    }

    #[test]
    fn query_repeat_open_same_rows() {
        let f = Fixture::new("q-repeat", &ten_lines());
        let filter = r#"{"pids":{"values":[100]}}"#;
        let one = open(&f.source, filter).unwrap();
        let two = open(&f.source, filter).unwrap();
        assert_eq!(one.row_count(), two.row_count());
        assert_eq!(one.page(2, 3).unwrap(), two.page(2, 3).unwrap());
    }

    #[test]
    fn query_two_queries_independent() {
        let f = Fixture::new("q-two", &lines());
        let one = open(&f.source, r#"{"pids":{"values":[100]}}"#).unwrap();
        let two = open(&f.source, r#"{"pids":{"values":[200]}}"#).unwrap();
        assert_eq!(one.row_count(), 2);
        assert_eq!(two.row_count(), 1);
        assert_eq!(one.row_count(), 2, "两个查询互不影响");
    }
}