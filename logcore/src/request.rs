//! 请求（plan 入参）schema 与字段校验（design D21）。
//! 未声明字段一律忽略；缺必填项或取值非法返回 `INVALID_ARG`，`field` 为点分路径。

use std::path::PathBuf;

use serde_json::Value;

use crate::error::{CoreError, CoreResult};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Kind {
    File,
    Device,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Mode {
    Off,
    On,
    Auto,
}

#[derive(Clone, Debug)]
pub struct ProxyCfg {
    pub pipe_name: String,
    pub launch_file: String,
    pub launch_args: Vec<String>,
    pub launch_timeout_ms: u64,
}

#[derive(Clone, Debug)]
pub struct SidecarCfg {
    pub dir: PathBuf,
    pub entry_limit_bytes: u64,
    pub dir_limit_bytes: u64,
}

#[derive(Clone, Debug)]
pub struct Options {
    pub probe_window_bytes: usize,
    pub probe_threshold: f64,
    pub temp_dir: PathBuf,
    pub sidecar: Option<SidecarCfg>,
}

#[derive(Clone, Debug)]
pub struct Request {
    pub kind: Kind,
    pub path: Option<String>,
    pub serial: Option<String>,
    pub mode: Mode,
    pub proxy: Option<ProxyCfg>,
    pub options: Options,
}

pub const DEFAULT_PROBE_WINDOW: usize = 8192;
pub const DEFAULT_PROBE_THRESHOLD: f64 = 0.10;
pub const DEFAULT_ENTRY_LIMIT: u64 = 1 << 30;
pub const DEFAULT_DIR_LIMIT: u64 = 4 << 30;
pub const PIPE_NAME_MAX_LEN: usize = 256;

pub fn parse(json: &str) -> CoreResult<Request> {
    let root: Value = serde_json::from_str(json).map_err(|_| CoreError::invalid_arg("request"))?;
    let obj = root
        .as_object()
        .ok_or_else(|| CoreError::invalid_arg("request"))?;

    let kind_str = obj
        .get("kind")
        .ok_or_else(|| CoreError::invalid_arg("kind"))?;
    let kind = match kind_str.as_str() {
        Some("File") => Kind::File,
        Some("Device") => Kind::Device,
        _ => return Err(CoreError::invalid_arg("kind")),
    };

    let mode = match obj.get("mode").and_then(Value::as_str) {
        Some("Off") => Mode::Off,
        Some("On") => Mode::On,
        Some("Auto") => Mode::Auto,
        // 缺省 Auto；非法值回退 Off（spec）
        None => Mode::Auto,
        Some(_) => Mode::Off,
    };

    let options = parse_options(obj.get("options"))?;

    let mut path = None;
    let mut proxy = None;
    let mut serial = None;

    match kind {
        Kind::File => {
            let raw = obj
                .get("path")
                .and_then(Value::as_str)
                .ok_or_else(|| CoreError::invalid_arg("path"))?;
            if raw.is_empty() {
                return Err(CoreError::invalid_arg("path"));
            }
            if !is_absolute_path(raw) {
                return Err(CoreError::invalid_arg("path"));
            }
            path = Some(raw.to_string());

            let proxy_obj = obj
                .get("proxy")
                .and_then(Value::as_object)
                .ok_or_else(|| CoreError::invalid_arg("proxy"))?;
            let pipe_name = required_string(proxy_obj.get("pipeName"), "proxy.pipeName")?;
            if is_derived_pipe_name_too_long(&pipe_name) {
                return Err(CoreError::invalid_arg("proxy.pipeName"));
            }
            let launch_file = required_string(proxy_obj.get("launchFile"), "proxy.launchFile")?;
            let launch_timeout_ms =
                required_number(proxy_obj.get("launchTimeoutMs"), "proxy.launchTimeoutMs")? as u64;
            if launch_timeout_ms == 0 {
                return Err(CoreError::invalid_arg("proxy.launchTimeoutMs"));
            }
            let launch_args = match proxy_obj.get("launchArgs") {
                None => Vec::new(),
                Some(v) => {
                    let arr = v
                        .as_array()
                        .ok_or_else(|| CoreError::invalid_arg("proxy.launchArgs"))?;
                    let mut out = Vec::with_capacity(arr.len());
                    for item in arr {
                        let s = item
                            .as_str()
                            .ok_or_else(|| CoreError::invalid_arg("proxy.launchArgs"))?;
                        out.push(s.to_string());
                    }
                    out
                }
            };
            proxy = Some(ProxyCfg {
                pipe_name,
                launch_file,
                launch_args,
                launch_timeout_ms,
            });

            if options.sidecar.is_none() {
                return Err(CoreError::invalid_arg("options.sidecar.dir"));
            }
        }
        Kind::Device => {
            serial = obj
                .get("device")
                .and_then(Value::as_object)
                .and_then(|d| d.get("serial"))
                .and_then(Value::as_str)
                .map(str::to_string);
        }
    }

    Ok(Request {
        kind,
        path,
        serial,
        mode,
        proxy,
        options,
    })
}

fn parse_options(value: Option<&Value>) -> CoreResult<Options> {
    let obj = value
        .and_then(Value::as_object)
        .ok_or_else(|| CoreError::invalid_arg("options"))?;

    let temp_dir = required_string(obj.get("tempDir"), "options.tempDir")?;
    if !is_absolute_path(&temp_dir) {
        return Err(CoreError::invalid_arg("options.tempDir"));
    }

    let probe_window_bytes = match obj.get("probeWindowBytes") {
        None => DEFAULT_PROBE_WINDOW,
        Some(v) => {
            let n = v
                .as_u64()
                .ok_or_else(|| CoreError::invalid_arg("options.probeWindowBytes"))?;
            if n == 0 {
                return Err(CoreError::invalid_arg("options.probeWindowBytes"));
            }
            n as usize
        }
    };

    let probe_threshold = match obj.get("probeThreshold") {
        None => DEFAULT_PROBE_THRESHOLD,
        Some(v) => {
            let f = v
                .as_f64()
                .ok_or_else(|| CoreError::invalid_arg("options.probeThreshold"))?;
            if !(0.0..=1.0).contains(&f) {
                return Err(CoreError::invalid_arg("options.probeThreshold"));
            }
            f
        }
    };

    let sidecar = match obj.get("sidecar") {
        None => None,
        Some(v) => {
            let sc = v
                .as_object()
                .ok_or_else(|| CoreError::invalid_arg("options.sidecar"))?;
            let dir = required_string(sc.get("dir"), "options.sidecar.dir")?;
            if !is_absolute_path(&dir) {
                return Err(CoreError::invalid_arg("options.sidecar.dir"));
            }
            let entry_limit_bytes = optional_u64(
                sc.get("entryLimitBytes"),
                "options.sidecar.entryLimitBytes",
                DEFAULT_ENTRY_LIMIT,
            )?;
            let dir_limit_bytes = optional_u64(
                sc.get("dirLimitBytes"),
                "options.sidecar.dirLimitBytes",
                DEFAULT_DIR_LIMIT,
            )?;
            Some(SidecarCfg {
                dir: PathBuf::from(dir),
                entry_limit_bytes,
                dir_limit_bytes,
            })
        }
    };

    Ok(Options {
        probe_window_bytes,
        probe_threshold,
        temp_dir: PathBuf::from(temp_dir),
        sidecar,
    })
}

fn required_string(value: Option<&Value>, field: &str) -> CoreResult<String> {
    let s = value
        .and_then(Value::as_str)
        .ok_or_else(|| CoreError::invalid_arg(field))?;
    if s.is_empty() {
        return Err(CoreError::invalid_arg(field));
    }
    Ok(s.to_string())
}

fn required_number(value: Option<&Value>, field: &str) -> CoreResult<i64> {
    let n = value
        .and_then(Value::as_i64)
        .ok_or_else(|| CoreError::invalid_arg(field))?;
    Ok(n)
}

fn optional_u64(value: Option<&Value>, field: &str, default: u64) -> CoreResult<u64> {
    match value {
        None => Ok(default),
        Some(v) => {
            let n = v
                .as_u64()
                .ok_or_else(|| CoreError::invalid_arg(field))?;
            if n == 0 {
                return Err(CoreError::invalid_arg(field));
            }
            Ok(n)
        }
    }
}

/// Windows 绝对路径判定：盘符绝对（`C:\...`）或 UNC（`\\server\share\...`）。
/// 盘符相对 `C:foo`、根相对 `\foo`、普通相对 `foo\bar` 一律非法。
pub fn is_absolute_path(path: &str) -> bool {
    let bytes: Vec<char> = path.chars().collect();
    if bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == ':'
        && (bytes[2] == '\\' || bytes[2] == '/')
    {
        return true;
    }
    if path.starts_with("\\\\") || path.starts_with("//") {
        // UNC 需要 server + share 两段
        let rest = &path[2..];
        let mut parts = rest.split(['\\', '/']).filter(|p| !p.is_empty());
        return parts.next().is_some() && parts.next().is_some();
    }
    false
}

/// core 自己拉起代理时使用派生名 `<基础名>-<pid>`，长度按该实际名判定（D6）。
pub fn is_derived_pipe_name_too_long(pipe_name: &str) -> bool {
    let derived_len = pipe_name.chars().count() + 1 + std::process::id().to_string().len();
    derived_len > PIPE_NAME_MAX_LEN
}

/// 构造 `\\?\` / `\\?\UNC\` 长路径形式（D14：仅用于实际打开，不改显示路径）。
pub fn long_path_for(display: &str) -> String {
    if display.starts_with("\\\\?\\") {
        return display.to_string();
    }
    if let Some(rest) = display.strip_prefix("\\\\") {
        return format!("\\\\?\\UNC\\{rest}");
    }
    format!("\\\\?\\{display}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{INVALID_ARG, OK};

    fn base_file_request() -> Value {
        serde_json::json!({
            "kind": "File",
            "path": "C:\\logs\\a.log",
            "proxy": {
                "pipeName": "logcat-fileproxy",
                "launchFile": "C:\\tools\\proxy.exe",
                "launchTimeoutMs": 5000
            },
            "options": {
                "tempDir": "C:\\temp",
                "sidecar": { "dir": "C:\\cache" }
            }
        })
    }

    fn request_error(value: &Value) -> Option<String> {
        let text = value.to_string();
        match parse(&text) {
            Ok(_) => None,
            Err(e) => {
                assert_eq!(e.code, INVALID_ARG);
                Some(e.params.get("field").and_then(|v| v.as_str()).unwrap_or("").to_string())
            }
        }
    }

    #[test]
    fn request_required_fields_rejected_with_field() {
        let mut v = base_file_request();
        v.as_object_mut().unwrap().remove("proxy");
        assert_eq!(request_error(&v).as_deref(), Some("proxy"));

        let mut v = base_file_request();
        v["proxy"].as_object_mut().unwrap().remove("pipeName");
        assert_eq!(request_error(&v).as_deref(), Some("proxy.pipeName"));

        let mut v = base_file_request();
        v.as_object_mut().unwrap().remove("path");
        assert_eq!(request_error(&v).as_deref(), Some("path"));

        let mut v = base_file_request();
        v["options"].as_object_mut().unwrap().remove("tempDir");
        assert_eq!(request_error(&v).as_deref(), Some("options.tempDir"));

        let mut v = base_file_request();
        v["options"].as_object_mut().unwrap().remove("sidecar");
        assert_eq!(request_error(&v).as_deref(), Some("options.sidecar.dir"));
    }

    #[test]
    fn request_defaults_applied() {
        let req = parse(&base_file_request().to_string()).unwrap();
        assert_eq!(req.options.probe_window_bytes, DEFAULT_PROBE_WINDOW);
        assert!((req.options.probe_threshold - DEFAULT_PROBE_THRESHOLD).abs() < f64::EPSILON);
        let sidecar = req.options.sidecar.unwrap();
        assert_eq!(sidecar.entry_limit_bytes, DEFAULT_ENTRY_LIMIT);
        assert_eq!(sidecar.dir_limit_bytes, DEFAULT_DIR_LIMIT);
        assert_eq!(req.mode, Mode::Auto);
        assert!(req.proxy.as_ref().unwrap().launch_args.is_empty());

        // 显式传入默认值与省略等价
        let mut v = base_file_request();
        v["options"]["probeWindowBytes"] = Value::from(DEFAULT_PROBE_WINDOW);
        v["options"]["probeThreshold"] = Value::from(DEFAULT_PROBE_THRESHOLD);
        let explicit = parse(&v.to_string()).unwrap();
        assert_eq!(explicit.options.probe_window_bytes, req.options.probe_window_bytes);
        assert_eq!(explicit.options.probe_threshold, req.options.probe_threshold);
    }

    #[test]
    fn request_unknown_fields_ignored() {
        let mut v = base_file_request();
        v["unknownTop"] = Value::from(1);
        v["options"]["unknownNested"] = Value::from("x");
        v["proxy"]["unknownProxy"] = Value::from(true);
        let req = parse(&v.to_string()).unwrap();
        assert_eq!(req.path.as_deref(), Some("C:\\logs\\a.log"));
    }

    #[test]
    fn request_relative_path_rejected() {
        for bad in ["logs\\a.log", "C:logs\\a.log", "\\logs\\a.log", "a.log", ""] {
            let mut v = base_file_request();
            v["path"] = Value::from(bad);
            assert_eq!(
                request_error(&v).as_deref(),
                Some("path"),
                "{bad} 应被拒为相对路径"
            );
        }
        // UNC 合法
        let mut v = base_file_request();
        v["path"] = Value::from("\\\\server\\share\\a.log");
        assert!(parse(&v.to_string()).is_ok());
    }

    #[test]
    fn request_derived_pipe_name_too_long_rejected() {
        let long = "p".repeat(PIPE_NAME_MAX_LEN);
        let mut v = base_file_request();
        v["proxy"]["pipeName"] = Value::from(long);
        assert_eq!(request_error(&v).as_deref(), Some("proxy.pipeName"));

        let just_right = "p".repeat(
            PIPE_NAME_MAX_LEN - 1 - std::process::id().to_string().len(),
        );
        let mut v = base_file_request();
        v["proxy"]["pipeName"] = Value::from(just_right);
        assert!(parse(&v.to_string()).is_ok(), "边界内应合法");
    }

    #[test]
    fn request_device_requires_only_temp_dir() {
        let v = serde_json::json!({
            "kind": "Device",
            "device": { "serial": "emulator-5554" },
            "mode": "Auto",
            "options": { "tempDir": "C:\\temp" }
        });
        let req = parse(&v.to_string()).unwrap();
        assert_eq!(req.kind, Kind::Device);
        assert!(req.path.is_none());
        assert!(req.proxy.is_none());
        assert_eq!(req.serial.as_deref(), Some("emulator-5554"));

        // 缺 tempDir 仍被拒
        let mut bad = v.clone();
        bad["options"].as_object_mut().unwrap().remove("tempDir");
        assert_eq!(request_error(&bad).as_deref(), Some("options.tempDir"));
    }

    #[test]
    fn request_numeric_types_not_string_compatible() {
        let mut v = base_file_request();
        v["proxy"]["launchTimeoutMs"] = Value::from("5000");
        assert_eq!(request_error(&v).as_deref(), Some("proxy.launchTimeoutMs"));
        let mut v = base_file_request();
        v["options"]["probeWindowBytes"] = Value::from("8192");
        assert_eq!(request_error(&v).as_deref(), Some("options.probeWindowBytes"));
        let mut v = base_file_request();
        v["options"]["probeThreshold"] = Value::from(1.5);
        assert_eq!(request_error(&v).as_deref(), Some("options.probeThreshold"));
        assert_eq!(code_name_ok(), OK);
    }

    fn code_name_ok() -> i32 {
        crate::error::OK
    }

    #[test]
    fn mode_values_fall_back_to_off() {
        let mut v = base_file_request();
        v["mode"] = Value::from("weird");
        assert_eq!(parse(&v.to_string()).unwrap().mode, Mode::Off);
        v["mode"] = Value::from("On");
        assert_eq!(parse(&v.to_string()).unwrap().mode, Mode::On);
    }

    #[test]
    fn long_path_prefix_forms() {
        assert_eq!(long_path_for("C:\\a\\b.log"), "\\\\?\\C:\\a\\b.log");
        assert_eq!(long_path_for("\\\\server\\share\\a.log"), "\\\\?\\UNC\\server\\share\\a.log");
        assert_eq!(long_path_for("\\\\?\\C:\\a"), "\\\\?\\C:\\a");
    }
}