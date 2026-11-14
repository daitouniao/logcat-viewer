//! 错误码数值表（design D15）。码值一经发布不改，新增只追加。
//! `params` 只放数据（字符串 / 数值），文案归宿主。

use serde_json::{Map, Value};

pub const OK: i32 = 0;
pub const BUSY: i32 = 1;
pub const STOPPED: i32 = 2;
pub const FAILED: i32 = 3;
pub const CLOSED: i32 = 4;
pub const INVALID_STATE: i32 = 5;
pub const INVALID_ARG: i32 = 6;
pub const INTERNAL: i32 = 7;
pub const PROXY_LAUNCH_TIMEOUT: i32 = 10;
pub const PROXY_DISCONNECT: i32 = 11;
pub const DIRECT_OPEN_FAILED: i32 = 12;
pub const NOT_FOUND: i32 = 13;
pub const UNSUPPORTED_FORMAT: i32 = 14;
pub const IO_ERROR: i32 = 15;
pub const PROXY_LAUNCH_FAILED: i32 = 16;

#[derive(Debug, Clone)]
pub struct CoreError {
    pub code: i32,
    pub params: Map<String, Value>,
}

pub type CoreResult<T> = Result<T, CoreError>;

impl CoreError {
    pub fn new(code: i32) -> Self {
        Self {
            code,
            params: Map::new(),
        }
    }

    pub fn invalid_arg(field: &str) -> Self {
        Self::new(INVALID_ARG).with("field", Value::String(field.to_string()))
    }

    pub fn with(mut self, key: &str, value: Value) -> Self {
        self.params.insert(key.to_string(), value);
        self
    }

    pub fn to_json(&self) -> Value {
        serde_json::json!({ "code": self.code, "params": self.params })
    }
}

/// 码名（排障与测试断言用）。
pub fn code_name(code: i32) -> &'static str {
    match code {
        OK => "OK",
        BUSY => "BUSY",
        STOPPED => "STOPPED",
        FAILED => "FAILED",
        CLOSED => "CLOSED",
        INVALID_STATE => "INVALID_STATE",
        INVALID_ARG => "INVALID_ARG",
        INTERNAL => "INTERNAL",
        PROXY_LAUNCH_TIMEOUT => "PROXY_LAUNCH_TIMEOUT",
        PROXY_DISCONNECT => "PROXY_DISCONNECT",
        DIRECT_OPEN_FAILED => "DIRECT_OPEN_FAILED",
        NOT_FOUND => "NOT_FOUND",
        UNSUPPORTED_FORMAT => "UNSUPPORTED_FORMAT",
        IO_ERROR => "IO_ERROR",
        PROXY_LAUNCH_FAILED => "PROXY_LAUNCH_FAILED",
        _ => "UNKNOWN",
    }
}

/// 每个码的 `params` 键集（D15 表）。`INVALID_ARG` 的 `field` 为必带键。
pub fn param_keys(code: i32) -> &'static [&'static str] {
    match code {
        OK | BUSY | STOPPED | FAILED | CLOSED | INVALID_STATE | INTERNAL => &[],
        INVALID_ARG => &["field"],
        PROXY_LAUNCH_TIMEOUT => &["proxyPath", "timeoutMs"],
        PROXY_DISCONNECT => &["reason"],
        DIRECT_OPEN_FAILED => &["path", "osError"],
        NOT_FOUND => &["path"],
        UNSUPPORTED_FORMAT => &["format"],
        IO_ERROR => &["path", "osError"],
        PROXY_LAUNCH_FAILED => &["proxyPath", "osError"],
        _ => &[],
    }
}

/// 附加在代理失败上的「直开也失败过」证据（D15 附注）。
pub const DIRECT_OPEN_ERROR_KEY: &str = "directOpenError";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn error_code_table_values_are_stable() {
        let table = [
            (OK, 0),
            (BUSY, 1),
            (STOPPED, 2),
            (FAILED, 3),
            (CLOSED, 4),
            (INVALID_STATE, 5),
            (INVALID_ARG, 6),
            (INTERNAL, 7),
            (PROXY_LAUNCH_TIMEOUT, 10),
            (PROXY_DISCONNECT, 11),
            (DIRECT_OPEN_FAILED, 12),
            (NOT_FOUND, 13),
            (UNSUPPORTED_FORMAT, 14),
            (IO_ERROR, 15),
            (PROXY_LAUNCH_FAILED, 16),
        ];
        for (code, expected) in table {
            assert_eq!(code, expected, "码值漂移：{}", code_name(code));
            assert_ne!(code_name(code), "UNKNOWN");
        }
        // 状态类 0–7、失败类 10–16，段间留空便于追加
        assert_eq!(code_name(8), "UNKNOWN");
        assert_eq!(code_name(9), "UNKNOWN");
        assert_eq!(code_name(17), "UNKNOWN");
    }

    #[test]
    fn error_params_schema_is_stable() {
        let expected: &[(i32, &[&str])] = &[
            (OK, &[]),
            (BUSY, &[]),
            (STOPPED, &[]),
            (FAILED, &[]),
            (CLOSED, &[]),
            (INVALID_STATE, &[]),
            (INVALID_ARG, &["field"]),
            (INTERNAL, &[]),
            (PROXY_LAUNCH_TIMEOUT, &["proxyPath", "timeoutMs"]),
            (PROXY_DISCONNECT, &["reason"]),
            (DIRECT_OPEN_FAILED, &["path", "osError"]),
            (NOT_FOUND, &["path"]),
            (UNSUPPORTED_FORMAT, &["format"]),
            (IO_ERROR, &["path", "osError"]),
            (PROXY_LAUNCH_FAILED, &["proxyPath", "osError"]),
        ];
        for (code, keys) in expected {
            assert_eq!(param_keys(*code), *keys, "params 键集漂移：{}", code_name(*code));
        }
        assert_eq!(DIRECT_OPEN_ERROR_KEY, "directOpenError");

        let e = CoreError::invalid_arg("options.tempDir");
        assert_eq!(e.code, INVALID_ARG);
        assert_eq!(e.params.get("field").and_then(|v| v.as_str()), Some("options.tempDir"));
    }
}