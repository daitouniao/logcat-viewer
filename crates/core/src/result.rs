//! 结果集：结果集序号（`ord`）与文档行号（`doc_row`）的两态映射。
//!
//! 恒等视图不复制行号；显式结果集持有升序文档行号表。

use std::sync::Arc;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ResultSet {
    /// 无过滤：`doc_row(ord) = ord`，零分配。
    Identity { rows: u32 },
    /// 过滤结果：严格升序的文档行号表。
    Rows(Arc<[u32]>),
}

impl ResultSet {
    pub fn identity(rows: u32) -> Self {
        Self::Identity { rows }
    }

    /// 构造显式结果集；`doc_row_count` 为文档总行数。
    pub fn from_rows(doc_rows: Vec<u32>, doc_row_count: u32) -> Self {
        debug_assert!(
            doc_rows.windows(2).all(|w| w[0] < w[1]),
            "结果集必须严格升序"
        );
        debug_assert!(
            doc_rows.iter().all(|&row| row < doc_row_count),
            "结果集元素必须小于文档行数"
        );
        Self::Rows(doc_rows.into())
    }

    pub fn len(&self) -> usize {
        match self {
            Self::Identity { rows } => *rows as usize,
            Self::Rows(rows) => rows.len(),
        }
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    pub fn doc_row(&self, ord: usize) -> u32 {
        debug_assert!(ord < self.len(), "ord 越界");
        match self {
            Self::Identity { .. } => ord as u32,
            Self::Rows(rows) => rows[ord],
        }
    }

    /// 向后取页起点：第一条文档行号不小于 `start_doc_row` 的结果序号；
    /// 没有这样的结果行返回 `None`（不回退到前一条）。
    pub fn forward_ord(&self, start_doc_row: u32) -> Option<usize> {
        match self {
            Self::Identity { rows } => {
                if start_doc_row < *rows {
                    Some(start_doc_row as usize)
                } else {
                    None
                }
            }
            Self::Rows(rows) => {
                let at = rows.partition_point(|&row| row < start_doc_row);
                (at < rows.len()).then_some(at)
            }
        }
    }

    /// 向上取页：文档行号严格小于 `end_doc_row` 的结果条数（即终点之前的连续前缀长度）。
    pub fn count_before(&self, end_doc_row: u32) -> usize {
        match self {
            Self::Identity { rows } => (end_doc_row as usize).min(*rows as usize),
            Self::Rows(rows) => rows.partition_point(|&row| row < end_doc_row),
        }
    }

    /// 真实行 → 结果集序号：命中返回该 `ord`；不命中取最近，平局取前；
    /// 越界取首/末；空结果集返回 `None`。
    pub fn resolve(&self, doc_row: u32) -> Option<u32> {
        match self {
            Self::Identity { rows } => {
                if *rows == 0 {
                    None
                } else {
                    Some(doc_row.min(rows - 1))
                }
            }
            Self::Rows(rows) => {
                if rows.is_empty() {
                    return None;
                }
                let at = rows.partition_point(|&row| row < doc_row);
                if at == rows.len() {
                    return Some((rows.len() - 1) as u32);
                }
                if rows[at] == doc_row {
                    return Some(at as u32);
                }
                if at == 0 {
                    return Some(0);
                }
                let prev = rows[at - 1];
                let next = rows[at];
                if next - doc_row < doc_row - prev {
                    Some(at as u32)
                } else {
                    Some((at - 1) as u32)
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rows() -> ResultSet {
        ResultSet::from_rows(vec![0, 2, 5], 10)
    }

    #[test]
    fn result_identity_maps_ord_to_ord() {
        let set = ResultSet::identity(10);
        assert_eq!(set.len(), 10);
        for ord in [0, 4, 9] {
            assert_eq!(set.doc_row(ord), ord as u32);
        }
    }

    #[test]
    fn result_rows_maps_ord_to_doc_row() {
        let set = rows();
        assert_eq!(set.doc_row(0), 0);
        assert_eq!(set.doc_row(1), 2);
        assert_eq!(set.doc_row(2), 5);
    }

    #[test]
    fn result_rows_len_matches_rows() {
        assert_eq!(rows().len(), 3);
        assert!(ResultSet::from_rows(Vec::new(), 10).is_empty());
    }

    #[test]
    fn result_forward_ord_rows() {
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        assert_eq!(set.forward_ord(0), Some(0), "首条不小于起点");
        assert_eq!(set.forward_ord(2), Some(0), "起点即命中");
        assert_eq!(set.forward_ord(3), Some(1), "落在结果行之间取后一条");
        assert_eq!(set.forward_ord(6), Some(2));
        assert_eq!(set.forward_ord(9), Some(2));
    }

    #[test]
    fn result_forward_ord_identity() {
        let set = ResultSet::identity(10);
        assert_eq!(set.forward_ord(0), Some(0));
        assert_eq!(set.forward_ord(4), Some(4));
        assert_eq!(set.forward_ord(9), Some(9));
    }

    #[test]
    fn result_forward_ord_past_end_none() {
        assert_eq!(ResultSet::from_rows(vec![2, 5, 9], 10).forward_ord(10), None);
        assert_eq!(ResultSet::identity(10).forward_ord(10), None);
    }

    #[test]
    fn result_count_before_rows() {
        let set = ResultSet::from_rows(vec![2, 5, 9], 10);
        assert_eq!(set.count_before(0), 0);
        assert_eq!(set.count_before(2), 0);
        assert_eq!(set.count_before(3), 1);
        assert_eq!(set.count_before(9), 2);
        assert_eq!(set.count_before(10), 3);
    }

    #[test]
    fn result_count_before_identity() {
        let set = ResultSet::identity(10);
        assert_eq!(set.count_before(0), 0);
        assert_eq!(set.count_before(4), 4);
        assert_eq!(set.count_before(10), 10);
        assert_eq!(set.count_before(99), 10);
    }

    #[test]
    fn result_count_before_zero_none() {
        assert_eq!(ResultSet::identity(0).count_before(0), 0);
        assert_eq!(ResultSet::identity(0).forward_ord(0), None);
        assert_eq!(ResultSet::from_rows(vec![0, 2], 10).count_before(0), 0);
    }

    #[test]
    fn resolve_identity_hit() {
        assert_eq!(ResultSet::identity(10).resolve(4), Some(4));
    }

    #[test]
    fn resolve_identity_clamps_last() {
        let set = ResultSet::identity(10);
        assert_eq!(set.resolve(9), Some(9));
        assert_eq!(set.resolve(99), Some(9));
    }

    #[test]
    fn resolve_identity_empty_returns_none() {
        assert_eq!(ResultSet::identity(0).resolve(3), None);
    }

    #[test]
    fn resolve_rows_hit_exact() {
        assert_eq!(rows().resolve(2), Some(1));
    }

    #[test]
    fn resolve_rows_between_prefers_nearest() {
        assert_eq!(rows().resolve(3), Some(1));
        assert_eq!(rows().resolve(4), Some(2));
    }

    #[test]
    fn resolve_rows_tie_prefers_previous() {
        assert_eq!(rows().resolve(1), Some(0));
    }

    #[test]
    fn resolve_rows_before_first_returns_first() {
        assert_eq!(rows().resolve(0), Some(0));
        assert_eq!(ResultSet::from_rows(vec![2, 5], 10).resolve(1), Some(0));
    }

    #[test]
    fn resolve_rows_after_last_returns_last() {
        assert_eq!(rows().resolve(9), Some(2));
    }

    #[test]
    fn resolve_rows_empty_returns_none() {
        assert_eq!(ResultSet::from_rows(Vec::new(), 10).resolve(3), None);
    }
}