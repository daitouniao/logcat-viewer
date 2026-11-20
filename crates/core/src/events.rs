//! 事件通道（design D22）与原子进度（design D16）。

use std::collections::VecDeque;
use std::sync::atomic::{AtomicI64, AtomicU64, AtomicU8, Ordering};
use std::sync::Mutex;

use serde_json::{json, Value};

use crate::error::CoreError;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Stage {
    Opening = 0,
    Spooling = 1,
    Indexing = 2,
    Capturing = 3,
    Draining = 4,
}

impl Stage {
    pub fn wire(self) -> &'static str {
        match self {
            Stage::Opening => "Opening",
            Stage::Spooling => "Spooling",
            Stage::Indexing => "Indexing",
            Stage::Capturing => "Capturing",
            Stage::Draining => "Draining",
        }
    }

    pub fn from_u8(v: u8) -> Stage {
        match v {
            1 => Stage::Spooling,
            2 => Stage::Indexing,
            3 => Stage::Capturing,
            4 => Stage::Draining,
            _ => Stage::Opening,
        }
    }
}

/// 原子进度快照：`total = -1` 表示 null。
#[derive(Debug)]
pub struct Progress {
    stage: AtomicU8,
    value: AtomicU64,
    total: AtomicI64,
}

impl Default for Progress {
    fn default() -> Self {
        Self {
            stage: AtomicU8::new(Stage::Opening as u8),
            value: AtomicU64::new(0),
            total: AtomicI64::new(-1),
        }
    }
}

impl Progress {
    pub fn set_stage(&self, stage: Stage, total: Option<u64>) {
        self.stage.store(stage as u8, Ordering::SeqCst);
        self.value.store(0, Ordering::SeqCst);
        self.total
            .store(total.map(|t| t as i64).unwrap_or(-1), Ordering::SeqCst);
    }

    /// 同一阶段内单调推进。
    pub fn advance(&self, value: u64) {
        let mut current = self.value.load(Ordering::SeqCst);
        while value > current {
            match self.value.compare_exchange(
                current,
                value,
                Ordering::SeqCst,
                Ordering::SeqCst,
            ) {
                Ok(_) => break,
                Err(actual) => current = actual,
            }
        }
    }

    pub fn add(&self, delta: u64) {
        self.value.fetch_add(delta, Ordering::SeqCst);
    }

    pub fn stage(&self) -> Stage {
        Stage::from_u8(self.stage.load(Ordering::SeqCst))
    }

    pub fn value(&self) -> u64 {
        self.value.load(Ordering::SeqCst)
    }

    pub fn total(&self) -> Option<u64> {
        match self.total.load(Ordering::SeqCst) {
            -1 => None,
            v => Some(v as u64),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({
            "stage": self.stage().wire(),
            "value": self.value(),
            "total": self.total().map(Value::from).unwrap_or(Value::Null),
        })
    }
}

#[derive(Clone, Debug)]
pub enum Event {
    Stage {
        stage: Stage,
        value: u64,
        total: Option<u64>,
    },
    Appended {
        rows: u64,
        total_rows: u64,
    },
    Stopped,
    Done,
    Error(CoreError),
}

impl Event {
    pub fn to_json(&self) -> Value {
        match self {
            Event::Stage {
                stage,
                value,
                total,
            } => json!({
                "type": "stage",
                "stage": stage.wire(),
                "value": value,
                "total": total.map(Value::from).unwrap_or(Value::Null),
            }),
            Event::Appended { rows, total_rows } => json!({
                "type": "appended",
                "rows": rows,
                "totalRows": total_rows,
            }),
            Event::Stopped => json!({ "type": "stopped" }),
            Event::Done => json!({ "type": "done" }),
            Event::Error(e) => json!({ "type": "error", "code": e.code, "params": e.params }),
        }
    }

    pub fn is_terminal(&self) -> bool {
        matches!(self, Event::Stopped | Event::Done | Event::Error(_))
    }

    pub fn kind(&self) -> &'static str {
        match self {
            Event::Stage { .. } => "stage",
            Event::Appended { .. } => "appended",
            Event::Stopped => "stopped",
            Event::Done => "done",
            Event::Error(_) => "error",
        }
    }
}

#[derive(Default, Debug)]
pub struct EventQueue {
    inner: Mutex<VecDeque<Event>>,
}

impl EventQueue {
    pub fn push(&self, event: Event) {
        self.inner.lock().unwrap().push_back(event);
    }

    /// 一次取走全部待处理事件（不合并、不丢弃）。
    pub fn drain_all(&self) -> Vec<Event> {
        let mut guard = self.inner.lock().unwrap();
        guard.drain(..).collect()
    }

    pub fn clear(&self) {
        self.inner.lock().unwrap().clear();
    }

    pub fn len(&self) -> usize {
        self.inner.lock().unwrap().len()
    }
}

pub fn events_json(events: &[Event]) -> Value {
    Value::Array(events.iter().map(Event::to_json).collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{CoreError, DIRECT_OPEN_FAILED, IO_ERROR};

    #[test]
    fn events_payload_matches_schema() {
        let stage = Event::Stage {
            stage: Stage::Indexing,
            value: 10,
            total: Some(100),
        };
        let appended = Event::Appended {
            rows: 5,
            total_rows: 15,
        };
        let error = Event::Error(
            CoreError::new(DIRECT_OPEN_FAILED)
                .with("path", Value::from("C:\\a.log"))
                .with("osError", Value::from(2)),
        );
        let json = events_json(&[stage, appended, Event::Stopped, Event::Done, error]);
        let arr = json.as_array().unwrap();
        assert_eq!(arr.len(), 5);
        assert_eq!(arr[0]["type"], "stage");
        assert_eq!(arr[0]["stage"], "Indexing");
        assert_eq!(arr[0]["value"], 10);
        assert_eq!(arr[0]["total"], 100);
        assert_eq!(arr[1]["type"], "appended");
        assert_eq!(arr[1]["rows"], 5);
        assert_eq!(arr[1]["totalRows"], 15);
        assert_eq!(arr[2], json!({"type":"stopped"}));
        assert_eq!(arr[3], json!({"type":"done"}));
        assert_eq!(arr[4]["type"], "error");
        assert_eq!(arr[4]["code"], DIRECT_OPEN_FAILED);
        assert_eq!(arr[4]["params"]["path"], "C:\\a.log");
    }

    #[test]
    fn poll_events_returns_all_pending() {
        let q = EventQueue::default();
        assert_eq!(events_json(&q.drain_all()), json!([]));
        q.push(Event::Done);
        q.push(Event::Appended { rows: 1, total_rows: 1 });
        let all = q.drain_all();
        assert_eq!(all.len(), 2);
        assert_eq!(q.len(), 0);
    }

    #[test]
    fn events_stage_progress_monotonic() {
        let p = Progress::default();
        assert_eq!(p.to_json()["stage"], "Opening");
        assert!(p.to_json()["total"].is_null());
        p.set_stage(Stage::Indexing, Some(100));
        p.advance(10);
        p.advance(5); // 不倒退
        assert_eq!(p.value(), 10);
        p.advance(50);
        assert_eq!(p.value(), 50);
        assert_eq!(p.total(), Some(100));
        p.set_stage(Stage::Spooling, None);
        assert_eq!(p.value(), 0, "切换阶段归零");
        assert_eq!(p.total(), None);
    }

    #[test]
    fn progress_total_null_when_unknown() {
        let p = Progress::default();
        p.set_stage(Stage::Capturing, None);
        let json = p.to_json();
        assert_eq!(json["stage"], "Capturing");
        assert_eq!(json["total"], Value::Null);
        assert_eq!(json["value"], 0);
    }

    #[test]
    fn events_error_params_match_schema() {
        let e = CoreError::new(IO_ERROR)
            .with("path", Value::from("C:\\t\\a.log"))
            .with("osError", Value::from(5));
        let json = Event::Error(e).to_json();
        assert_eq!(json["type"], "error");
        assert_eq!(json["params"]["osError"], 5);
        assert!(Event::Error(CoreError::new(IO_ERROR)).is_terminal());
        assert!(!Event::Appended { rows: 0, total_rows: 0 }.is_terminal());
    }

    #[test]
    fn event_types_are_five() {
        // D22：事件清单只有 stage / appended / stopped / done / error
        let kinds = [
            Event::Stage { stage: Stage::Opening, value: 0, total: None }.kind(),
            Event::Appended { rows: 1, total_rows: 1 }.kind(),
            Event::Stopped.kind(),
            Event::Done.kind(),
            Event::Error(CoreError::new(IO_ERROR)).kind(),
        ];
        assert_eq!(kinds, ["stage", "appended", "stopped", "done", "error"]);
    }

    #[test]
    fn stage_wire_values_are_five() {
        let all = [
            Stage::Opening,
            Stage::Spooling,
            Stage::Indexing,
            Stage::Capturing,
            Stage::Draining,
        ];
        for (i, s) in all.iter().enumerate() {
            assert_eq!(Stage::from_u8(i as u8), *s);
            assert_eq!(Stage::from_u8(*s as u8), *s);
        }
        assert_eq!(Stage::from_u8(9), Stage::Opening);
    }
}