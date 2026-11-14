//! 测试代理进程：按参数起一个命名管道对端，供 core 的拉起路径联测。
//!
//! 参数：`--pipe <name>`、`--serve <dir>`、`--record <file>`、`--hold-ms <n>`、
//! `--pace-ms <n>`、`--proto <n>`。`--record` 把完整 argv 写入文件（验证派生管道名）。

use std::sync::Arc;
use std::time::Duration;

use logcore::winserver::{spawn_listener, ServeOptions};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let value = |key: &str| -> Option<String> {
        args.iter()
            .position(|a| a == key)
            .and_then(|i| args.get(i + 1))
            .cloned()
    };
    let number = |key: &str| -> u64 { value(key).and_then(|v| v.parse().ok()).unwrap_or(0) };

    if let Some(record) = value("--record") {
        let _ = std::fs::write(record, args.join("\n"));
    }

    let pipe = match value("--pipe") {
        Some(p) => p,
        None => {
            eprintln!("logcore-test-proxy: --pipe 必填");
            std::process::exit(2);
        }
    };

    let hold = number("--hold-ms");
    if hold > 0 {
        std::thread::sleep(Duration::from_millis(hold));
    }

    let opts = Arc::new(ServeOptions {
        serve_dir: value("--serve"),
        pace_ms: number("--pace-ms"),
        proto: value("--proto").and_then(|v| v.parse().ok()).unwrap_or(1),
    });
    let (_stop, join) = spawn_listener(&pipe, opts);
    let _ = join.join();
}