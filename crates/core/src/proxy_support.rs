//! 测试代理对端：进程内监听器 + `logcore-test-proxy` 可执行文件定位。

use std::io::{Read, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;

use crate::winserver::{spawn_listener, ServeOptions};

pub fn test_proxy_exe() -> String {
    let current = std::env::current_exe().expect("当前测试进程路径");
    let bin_dir = current
        .parent()
        .and_then(|p| p.parent())
        .expect("测试可执行目录")
        .to_path_buf();
    let name = if cfg!(windows) {
        "logcore-test-proxy.exe"
    } else {
        "logcore-test-proxy"
    };
    let exe = bin_dir.join(name);
    if exe.exists() {
        return exe.to_string_lossy().to_string();
    }
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".to_string());
    let status = std::process::Command::new(&cargo)
        .args(["build", "--quiet", "-p", "logcore", "--bin", "logcore-test-proxy"])
        .current_dir(env!("CARGO_MANIFEST_DIR"))
        .status();
    assert!(
        matches!(status, Ok(s) if s.success()),
        "构建测试代理失败：{}",
        exe.display()
    );
    if exe.exists() {
        return exe.to_string_lossy().to_string();
    }
    // `cargo test --target-dir <dir>`（如覆盖率运行）下产物不在测试可执行目录旁
    let built = cargo_target_dir(&cargo).join("debug").join(name);
    assert!(built.exists(), "找不到测试代理：{}", built.display());
    built.to_string_lossy().to_string()
}

fn cargo_target_dir(cargo: &str) -> PathBuf {
    let out = std::process::Command::new(cargo)
        .args(["metadata", "--format-version", "1", "--no-deps", "--quiet"])
        .current_dir(env!("CARGO_MANIFEST_DIR"))
        .output()
        .expect("执行 cargo metadata");
    let value: serde_json::Value =
        serde_json::from_slice(&out.stdout).expect("解析 cargo metadata 输出");
    PathBuf::from(value["target_directory"].as_str().expect("target_directory"))
}

pub struct TestProxyServer {
    pipe: String,
    stop: Arc<AtomicBool>,
    join: Option<JoinHandle<()>>,
}

impl TestProxyServer {
    pub fn start(pipe: &str) -> Self {
        Self::start_with(pipe, 1, None, 0)
    }

    pub fn start_with_proto(pipe: &str, proto: i32) -> Self {
        Self::start_with(pipe, proto, None, 0)
    }

    /// 带文件根目录：请求路径打不开时按 basename 回退（模拟「直开失败但代理可读」）。
    pub fn start_with_dir(pipe: &str, serve_dir: &std::path::Path) -> Self {
        Self::start_with(pipe, 1, Some(serve_dir.to_string_lossy().to_string()), 0)
    }

    pub fn start_with(pipe: &str, proto: i32, serve_dir: Option<String>, pace_ms: u64) -> Self {
        let opts = Arc::new(ServeOptions {
            serve_dir,
            pace_ms,
            proto,
        });
        let (stop, join) = spawn_listener(pipe, opts);
        Self {
            pipe: pipe.to_string(),
            stop,
            join: Some(join),
        }
    }

    /// 直接握手，验证对端仍可服务（不依赖 core 的连接登记）。
    pub fn is_alive(&self) -> bool {
        hello(&self.pipe).is_ok()
    }
}

impl Drop for TestProxyServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        // 监听线程可能阻塞在 ConnectNamedPipe：以一个哑连接把它唤醒后再 join。
        let _ = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(format!("\\\\.\\pipe\\{}", self.pipe));
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

fn hello(pipe: &str) -> std::io::Result<serde_json::Value> {
    let path = format!("\\\\.\\pipe\\{pipe}");
    let mut f = std::fs::OpenOptions::new().read(true).write(true).open(path)?;
    f.write_all(br#"{"id":1,"op":"hello"}"#)?;
    f.write_all(b"\n")?;
    f.flush()?;
    let mut buf = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        let n = f.read(&mut byte)?;
        if n == 0 {
            return Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "对端关闭",
            ));
        }
        if byte[0] == b'\n' {
            break;
        }
        buf.push(byte[0]);
    }
    serde_json::from_slice(&buf)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e.to_string()))
}

pub fn temp_dir_for(tag: &str) -> PathBuf {
    let dir = crate::testutil::temp_path(tag);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}