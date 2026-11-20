//! logcore 调试壳：plan 对拍 oracle。
//!
//! `logcore_cli plan <文件> [--mode Auto|On|Off] [--sidecar <目录>]` 打印 plan 结果 JSON；
//! 失败时打印错误码与参数并以非零码退出。

use std::path::PathBuf;

use logcore_core::error::code_name;
use logcore_core::source::SourceHandle;

fn main() {
    let mut args = std::env::args().skip(1);
    match args.next().as_deref() {
        Some("--version") | Some("-V") => println!("logcore_cli {}", env!("CARGO_PKG_VERSION")),
        Some("plan") => std::process::exit(plan(args.collect())),
        Some("--help") | Some("-h") | None => usage(),
        Some(other) => {
            eprintln!("未知子命令：{other}");
            usage();
            std::process::exit(2);
        }
    }
}

fn usage() {
    eprintln!("用法：logcore_cli plan <文件> [--mode Auto|On|Off] [--sidecar <目录>]");
}

fn plan(args: Vec<String>) -> i32 {
    let mut path: Option<PathBuf> = None;
    let mut mode = String::from("Auto");
    let mut sidecar: Option<PathBuf> = None;
    let mut iter = args.into_iter();
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--mode" => match iter.next() {
                Some(value) => mode = value,
                None => {
                    eprintln!("--mode 缺少取值");
                    return 2;
                }
            },
            "--sidecar" => match iter.next() {
                Some(value) => sidecar = Some(PathBuf::from(value)),
                None => {
                    eprintln!("--sidecar 缺少取值");
                    return 2;
                }
            },
            flag if flag.starts_with("--") => {
                eprintln!("未知参数：{flag}");
                return 2;
            }
            _ => path = Some(PathBuf::from(arg)),
        }
    }
    let Some(path) = path else {
        usage();
        return 2;
    };

    let temp = std::env::temp_dir().join(format!("logcore-cli-{}", std::process::id()));
    let mut options = serde_json::json!({ "tempDir": temp.to_string_lossy() });
    if let Some(dir) = sidecar {
        options["sidecar"] = serde_json::json!({ "dir": dir.to_string_lossy() });
    }
    let request = serde_json::json!({
        "kind": "File",
        "path": path.to_string_lossy(),
        "mode": mode,
        "proxy": {
            "pipeName": format!("logcore-cli-{}", std::process::id()),
            "launchFile": "<proxy>",
            "launchArgs": [],
            "launchTimeoutMs": 0
        },
        "options": options
    });

    match SourceHandle::plan(&request.to_string()) {
        Ok(handle) => {
            println!("{}", handle.plan_json());
            0
        }
        Err(err) => {
            eprintln!("{} {}", code_name(err.code), err.to_json());
            1
        }
    }
}
