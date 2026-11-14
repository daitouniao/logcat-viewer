//! 测试辅助：唯一名、临时路径与请求 JSON 构造。

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub fn rand_suffix() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!("{}-{}", std::process::id(), nanos)
}

pub fn unique_pipe(tag: &str) -> String {
    format!("logcore-test-{tag}-{}", rand_suffix())
}

pub fn temp_path(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("logcore-{tag}-{}", rand_suffix()))
}

pub fn temp_dir(tag: &str) -> PathBuf {
    let dir = temp_path(tag);
    fs::create_dir_all(&dir).unwrap();
    dir
}

/// 写一份已知文本日志（LF 行尾）。
pub fn text_file(dir: &Path, name: &str, lines: &[&str]) -> PathBuf {
    let mut data = String::new();
    for line in lines {
        data.push_str(line);
        data.push('\n');
    }
    let path = dir.join(name);
    fs::write(&path, data).unwrap();
    path
}

pub fn file_request(
    path: &Path,
    temp: &Path,
    cache: &Path,
    pipe: &str,
    launch_file: &str,
    launch_args: &[String],
    launch_timeout_ms: u64,
    mode: &str,
) -> String {
    // `launchFile` 契约要求非空；本地直开路径不使用它，用占位符满足形状。
    let launch_file = if launch_file.is_empty() {
        "<proxy>"
    } else {
        launch_file
    };
    serde_json::json!({
        "kind": "File",
        "path": path.to_string_lossy(),
        "mode": mode,
        "proxy": {
            "pipeName": pipe,
            "launchFile": launch_file,
            "launchArgs": launch_args,
            "launchTimeoutMs": launch_timeout_ms
        },
        "options": {
            "tempDir": temp.to_string_lossy(),
            "sidecar": { "dir": cache.to_string_lossy() }
        }
    })
    .to_string()
}

pub fn device_request(temp: &Path) -> String {
    serde_json::json!({
        "kind": "Device",
        "device": { "serial": "test-serial" },
        "mode": "Auto",
        "options": { "tempDir": temp.to_string_lossy() }
    })
    .to_string()
}

/// 轮询等待条件成立（默认 10 s，2 ms 间隔）。
pub fn wait_until(cond: impl Fn() -> bool) -> bool {
    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    while std::time::Instant::now() < deadline {
        if cond() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    cond()
}