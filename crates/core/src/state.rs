//! 句柄状态机与「状态 × 操作」矩阵（design D23）。
//! 表外组合一律 `INVALID_STATE`；`CLOSED` 列的各读取入口返回 `CLOSED`。

use crate::error::{CoreError, CoreResult, CLOSED, FAILED, INVALID_STATE, STOPPED};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum State {
    PlanReady,
    Committing,
    Loaded,
    Running,
    Stopping,
    Stopped,
    Failed,
    Closed,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Op {
    Commit,
    Feed,
    Stop,
    Close,
    PollEvents,
    GetProgress,
    GetDescriptor,
    Cancel,
    FreeHandle,
    RowCount,
    Page,
    OpenQuery,
}

impl Op {
    pub fn name(self) -> &'static str {
        match self {
            Op::Commit => "commit",
            Op::Feed => "feed",
            Op::Stop => "stop",
            Op::Close => "close",
            Op::PollEvents => "poll_events",
            Op::GetProgress => "get_progress",
            Op::GetDescriptor => "get_descriptor",
            Op::Cancel => "cancel",
            Op::FreeHandle => "free_handle",
            Op::RowCount => "row_count",
            Op::Page => "page",
            Op::OpenQuery => "open_query",
        }
    }
}

/// 按 D23 矩阵判定；`Ok(())` 表示允许继续执行。
pub fn check(state: State, op: Op) -> CoreResult<()> {
    use Op::*;
    use State::*;
    let code = match (op, state) {
        // close 在任何状态合法（幂等）；free_handle 仅 Closed 合法
        (Close, _) => return Ok(()),
        (FreeHandle, Closed) => return Ok(()),
        (FreeHandle, _) => INVALID_STATE,
        // 其余操作在 Closed 上的返回码
        (_, Closed) => CLOSED,
        (RowCount, Loaded) | (Page, Loaded) | (OpenQuery, Loaded) => return Ok(()),
        (RowCount, _) | (Page, _) | (OpenQuery, _) => INVALID_STATE,
        (Commit, PlanReady) => return Ok(()),
        (Commit, _) => INVALID_STATE,
        (Feed, Running) => return Ok(()),
        (Feed, Stopping) | (Feed, Stopped) => STOPPED,
        (Feed, Failed) => FAILED,
        (Feed, _) => INVALID_STATE,
        (Stop, Running) | (Stop, Stopping) | (Stop, Stopped) => return Ok(()),
        (Stop, Failed) => FAILED,
        (Stop, _) => INVALID_STATE,
        (PollEvents, PlanReady) | (PollEvents, _) => return Ok(()),
        (GetProgress, _) => return Ok(()),
        (GetDescriptor, PlanReady) => INVALID_STATE,
        (GetDescriptor, _) => return Ok(()),
        (Cancel, PlanReady) | (Cancel, Committing) => return Ok(()),
        (Cancel, _) => INVALID_STATE,
    };
    Err(CoreError::new(code))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::code_name;

    const STATES: [State; 8] = [
        State::PlanReady,
        State::Committing,
        State::Loaded,
        State::Running,
        State::Stopping,
        State::Stopped,
        State::Failed,
        State::Closed,
    ];

    fn allowed(state: State, op: Op) -> Option<i32> {
        check(state, op).err().map(|e| e.code)
    }

    #[test]
    fn state_commit_twice_invalid() {
        assert_eq!(allowed(State::PlanReady, Op::Commit), None);
        assert_eq!(allowed(State::Committing, Op::Commit), Some(INVALID_STATE));
        assert_eq!(allowed(State::Loaded, Op::Commit), Some(INVALID_STATE));
        assert_eq!(allowed(State::Closed, Op::Commit), Some(CLOSED));
    }

    #[test]
    fn state_open_query_only_loaded() {
        for state in STATES {
            if state == State::Loaded {
                assert_eq!(allowed(state, Op::OpenQuery), None, "Loaded 放行");
            } else if state == State::Closed {
                assert_eq!(allowed(state, Op::OpenQuery), Some(CLOSED));
            } else {
                assert_eq!(allowed(state, Op::OpenQuery), Some(INVALID_STATE));
            }
        }
    }

    #[test]
    fn state_free_before_close_invalid() {
        for state in STATES {
            if state == State::Closed {
                assert_eq!(allowed(state, Op::FreeHandle), None);
            } else {
                assert_eq!(
                    allowed(state, Op::FreeHandle),
                    Some(INVALID_STATE),
                    "{} 上 free 应被拒",
                    "state"
                );
            }
        }
    }

    #[test]
    fn state_matrix_matches_d23() {
        // 逐格表（D23 矩阵的完整副本，防止实现随手放宽）
        let table: [(Op, [Option<i32>; 8]); 12] = [
            (
                Op::Commit,
                [None, Some(5), Some(5), Some(5), Some(5), Some(5), Some(5), Some(4)],
            ),
            (
                Op::Feed,
                [Some(5), Some(5), Some(5), None, Some(2), Some(2), Some(3), Some(4)],
            ),
            (
                Op::Stop,
                [Some(5), Some(5), Some(5), None, None, None, Some(3), Some(4)],
            ),
            (
                Op::Close,
                [None, None, None, None, None, None, None, None],
            ),
            (
                Op::PollEvents,
                [None, None, None, None, None, None, None, Some(4)],
            ),
            (
                Op::GetProgress,
                [None, None, None, None, None, None, None, Some(4)],
            ),
            (
                Op::GetDescriptor,
                [Some(5), None, None, None, None, None, None, Some(4)],
            ),
            (
                Op::Cancel,
                [None, None, Some(5), Some(5), Some(5), Some(5), Some(5), Some(4)],
            ),
            (
                Op::FreeHandle,
                [Some(5), Some(5), Some(5), Some(5), Some(5), Some(5), Some(5), None],
            ),
            (
                Op::RowCount,
                [Some(5), Some(5), None, Some(5), Some(5), Some(5), Some(5), Some(4)],
            ),
            (
                Op::Page,
                [Some(5), Some(5), None, Some(5), Some(5), Some(5), Some(5), Some(4)],
            ),
            (
                Op::OpenQuery,
                [Some(5), Some(5), None, Some(5), Some(5), Some(5), Some(5), Some(4)],
            ),
        ];
        for (op, row) in table {
            for (i, state) in STATES.iter().enumerate() {
                assert_eq!(
                    allowed(*state, op),
                    row[i],
                    "{} × {} 不合 D23（期望 {:?}）",
                    op.name(),
                    code_name(row[i].unwrap_or(0)),
                    row[i]
                );
            }
        }
    }
}