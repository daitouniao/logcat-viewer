//! 文件代理管道客户端（协议 v1）+ 实例登记与单飞拉起（design D6）。
//!
//! 传输 = Windows 命名管道字节流：控制消息单行 JSON + `\n`；read 响应头行给出负载字节数，
//! 紧跟裸块。连接时先完成 hello 握手（proto 必须等于 1），握手失败不尝试拉起——
//! 有东西在监听但它不是预期代理，覆盖它会破坏对方的进程。

use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use serde_json::Value;

use crate::error::{
    CoreError, CoreResult, DIRECT_OPEN_ERROR_KEY, PROXY_DISCONNECT, PROXY_LAUNCH_FAILED,
    PROXY_LAUNCH_TIMEOUT,
};

pub const PROTO_VERSION: i64 = 1;
pub const MAX_READ_LEN: usize = 16 * 1024 * 1024;
pub const MAX_LINE_BYTES: usize = 64 * 1024;
pub const TRY_CONNECT_MS: u64 = 200;
pub const POLL_MS: u64 = 20;
pub const CHUNK_BYTES: usize = 8 * 1024 * 1024;

pub struct Pipe {
    f: File,
    buf: Vec<u8>,
    start: usize,
    end: usize,
}

impl Pipe {
    fn send_line(&mut self, line: &str) -> std::io::Result<()> {
        self.f.write_all(line.as_bytes())?;
        self.f.write_all(b"\n")?;
        self.f.flush()
    }

    fn read_line(&mut self) -> std::io::Result<String> {
        let mut out: Vec<u8> = Vec::new();
        loop {
            if self.start >= self.end {
                self.start = 0;
                self.end = 0;
                let n = self.f.read(&mut self.buf)?;
                if n == 0 {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::UnexpectedEof,
                        "代理连接中断",
                    ));
                }
                self.end = n;
            }
            let slice = &self.buf[self.start..self.end];
            if let Some(pos) = slice.iter().position(|b| *b == b'\n') {
                out.extend_from_slice(&slice[..pos]);
                self.start += pos + 1;
                return Ok(String::from_utf8_lossy(&out).to_string());
            }
            out.extend_from_slice(slice);
            self.start = self.end;
            if out.len() > MAX_LINE_BYTES {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "控制消息超过单行上限",
                ));
            }
        }
    }

    fn read_exact_bytes(&mut self, dst: &mut [u8]) -> std::io::Result<()> {
        let mut filled = 0;
        if self.start < self.end {
            let take = dst.len().min(self.end - self.start);
            dst[..take].copy_from_slice(&self.buf[self.start..self.start + take]);
            self.start += take;
            filled = take;
        }
        while filled < dst.len() {
            let n = self.f.read(&mut dst[filled..])?;
            if n == 0 {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    "收取裸块时流已结束",
                ));
            }
            filled += n;
        }
        Ok(())
    }
}

enum ConnectError {
    /// 管道不存在 / 全部实例忙 / 拒绝访问：按「代理未运行」处理（可拉起）。
    Unavailable,
    /// 连上了但 hello 握手失败：不覆盖对方进程。
    Rejected(CoreError),
}

pub struct ProxyClient {
    pipe: Pipe,
    next_id: i64,
}

impl ProxyClient {
    fn connect_once(pipe_name: &str) -> Result<Self, ConnectError> {
        let path = format!("\\\\.\\pipe\\{pipe_name}");
        let file = match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f) => f,
            Err(_) => return Err(ConnectError::Unavailable),
        };
        let mut client = ProxyClient {
            pipe: Pipe {
                f: file,
                buf: vec![0u8; 8192],
                start: 0,
                end: 0,
            },
            next_id: 1,
        };
        let resp = client
            .send("hello", Vec::new())
            .map_err(|e| ConnectError::Rejected(disconnect(&e)))?;
        if resp["ok"].as_bool() != Some(true) {
            return Err(ConnectError::Rejected(disconnect_msg("代理拒绝了 hello")));
        }
        if resp["proto"].as_i64() != Some(PROTO_VERSION) {
            return Err(ConnectError::Rejected(disconnect_msg("代理协议版本不匹配")));
        }
        Ok(client)
    }

    fn send(&mut self, op: &str, fields: Vec<(&str, Value)>) -> std::io::Result<Value> {
        let id = self.next_id;
        self.next_id += 1;
        let mut obj = serde_json::Map::new();
        obj.insert("id".into(), Value::from(id));
        obj.insert("op".into(), Value::from(op));
        for (k, v) in fields {
            obj.insert(k.into(), v);
        }
        let line = Value::Object(obj).to_string();
        if line.len() > MAX_LINE_BYTES {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "控制消息超长",
            ));
        }
        self.pipe.send_line(&line)?;
        let resp_line = self.pipe.read_line()?;
        let resp: Value = serde_json::from_str(&resp_line).map_err(|_| {
            std::io::Error::new(std::io::ErrorKind::InvalidData, "代理响应不是合法 JSON")
        })?;
        if resp["id"].as_i64() != Some(id) {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "代理响应 ID 不匹配",
            ));
        }
        Ok(resp)
    }

    pub fn open(&mut self, path: &str) -> CoreResult<(i64, u64)> {
        let resp = self
            .send("open", vec![("path", Value::from(path))])
            .map_err(|e| disconnect(&e))?;
        if resp["ok"].as_bool() != Some(true) {
            let reason = resp["err"].as_str().unwrap_or("open 失败").to_string();
            return Err(CoreError::new(PROXY_DISCONNECT).with("reason", Value::from(reason)));
        }
        let h = resp["h"].as_i64().unwrap_or(-1);
        let size = resp["size"].as_u64().unwrap_or(0);
        Ok((h, size))
    }

    pub fn read(&mut self, handle: i64, offset: u64, len: usize) -> CoreResult<Vec<u8>> {
        let len = len.min(MAX_READ_LEN);
        let resp = self
            .send(
                "read",
                vec![
                    ("h", Value::from(handle)),
                    ("off", Value::from(offset)),
                    ("len", Value::from(len)),
                ],
            )
            .map_err(|e| disconnect(&e))?;
        if resp["ok"].as_bool() != Some(true) {
            let reason = resp["err"].as_str().unwrap_or("read 失败").to_string();
            return Err(CoreError::new(PROXY_DISCONNECT).with("reason", Value::from(reason)));
        }
        let n = resp["n"].as_u64().ok_or_else(|| {
            CoreError::new(PROXY_DISCONNECT).with("reason", Value::from("响应缺 n"))
        })? as usize;
        if n > MAX_READ_LEN {
            return Err(CoreError::new(PROXY_DISCONNECT)
                .with("reason", Value::from("负载长度越界")));
        }
        let mut buf = vec![0u8; n];
        if n > 0 {
            self.pipe
                .read_exact_bytes(&mut buf)
                .map_err(|e| disconnect(&e))?;
        }
        Ok(buf)
    }

    pub fn close_handle(&mut self, handle: i64) {
        let _ = self.send("close", vec![("h", Value::from(handle))]);
    }

    /// 存活探测：零长 read，代理正常应答即连接仍在（D23 受理前校验）。
    pub fn probe_alive(&mut self, handle: i64) -> CoreResult<()> {
        let resp = self
            .send(
                "read",
                vec![
                    ("h", Value::from(handle)),
                    ("off", Value::from(0u64)),
                    ("len", Value::from(0u64)),
                ],
            )
            .map_err(|e| disconnect(&e))?;
        if resp["ok"].as_bool() != Some(true) {
            return Err(disconnect_msg("计划阶段的代理连接已断开"));
        }
        Ok(())
    }
}

fn disconnect(e: &std::io::Error) -> CoreError {
    disconnect_msg(&e.to_string())
}

fn disconnect_msg(msg: &str) -> CoreError {
    CoreError::new(PROXY_DISCONNECT).with("reason", Value::from(msg))
}

struct Instance {
    child: Option<Child>,
    users: usize,
}

fn registry() -> &'static Mutex<HashMap<String, Instance>> {
    static REG: OnceLock<Mutex<HashMap<String, Instance>>> = OnceLock::new();
    REG.get_or_init(|| Mutex::new(HashMap::new()))
}

pub struct ProxyLease {
    client: ProxyClient,
    pub handle: i64,
    pub size: u64,
    pipe_name: String,
    owned: bool,
    released: bool,
}

impl ProxyLease {
    pub fn client(&mut self) -> &mut ProxyClient {
        &mut self.client
    }

    pub fn release(&mut self) {
        if self.released {
            return;
        }
        self.released = true;
        if self.handle != 0 {
            self.client.close_handle(self.handle);
        }
        if !self.owned {
            return;
        }
        let mut reg = registry().lock().unwrap();
        if let Some(inst) = reg.get_mut(&self.pipe_name) {
            inst.users = inst.users.saturating_sub(1);
            if inst.users == 0 {
                if let Some(mut child) = reg.remove(&self.pipe_name).and_then(|i| i.child) {
                    let _ = child.kill();
                    let _ = child.wait();
                }
            }
        }
    }
}

impl Drop for ProxyLease {
    fn drop(&mut self) {
        self.release();
    }
}

enum TryConnect {
    Connected(ProxyClient),
    Unavailable,
    Rejected(CoreError),
}

/// 在 `timeout_ms` 内反复尝试连接（管道实例可能正在创建）。
fn try_connect(pipe_name: &str, timeout_ms: u64) -> TryConnect {
    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    loop {
        match ProxyClient::connect_once(pipe_name) {
            Ok(c) => return TryConnect::Connected(c),
            Err(ConnectError::Rejected(e)) => return TryConnect::Rejected(e),
            Err(ConnectError::Unavailable) => {
                if Instant::now() >= deadline {
                    return TryConnect::Unavailable;
                }
                std::thread::sleep(Duration::from_millis(10));
            }
        }
    }
}

fn spawn_args(launch_args: &[String], pipe_name: &str) -> Vec<String> {
    if launch_args.iter().any(|a| a.contains("{pipe}")) {
        return launch_args
            .iter()
            .map(|a| a.replace("{pipe}", pipe_name))
            .collect();
    }
    let mut args = launch_args.to_vec();
    args.push("--pipe".to_string());
    args.push(pipe_name.to_string());
    args
}

/// 取得代理连接：先试连基础名（200 ms，外部代理）→ 连不上且配置了启动命令则用派生名拉起。
/// 只负责连接；`open` 由 [`open_file`] 在拿到路径后执行（plan/commit 共用）。
pub fn acquire(
    pipe_name: &str,
    launch_file: &str,
    launch_args: &[String],
    launch_timeout_ms: u64,
    direct_error: Option<&CoreError>,
) -> CoreResult<ProxyLease> {
    match try_connect(pipe_name, TRY_CONNECT_MS) {
        TryConnect::Connected(client) => {
            return Ok(ProxyLease {
                client,
                handle: 0,
                size: 0,
                pipe_name: pipe_name.to_string(),
                owned: false,
                released: false,
            })
        }
        TryConnect::Rejected(e) => return Err(with_direct(e, direct_error)),
        TryConnect::Unavailable => {}
    }

    if launch_file.is_empty() {
        return Err(with_direct(
            CoreError::new(PROXY_LAUNCH_FAILED)
                .with("proxyPath", Value::from(""))
                .with("osError", Value::from(0)),
            direct_error,
        ));
    }

    let derived = format!("{pipe_name}-{}", std::process::id());

    // 单飞：登记表锁覆盖「查重 + 拉起 + 等就绪」，并发需要只产生一次 spawn。
    let mut reg = registry().lock().unwrap();
    let existing_user = reg.get_mut(&derived).map(|inst| {
        inst.users += 1;
    });
    if existing_user.is_some() {
        drop(reg);
        return match try_connect(&derived, launch_timeout_ms) {
            TryConnect::Connected(client) => Ok(ProxyLease {
                client,
                handle: 0,
                size: 0,
                pipe_name: derived,
                owned: true,
                released: false,
            }),
            TryConnect::Rejected(e) => Err(with_direct(e, direct_error)),
            TryConnect::Unavailable => {
                let mut reg = registry().lock().unwrap();
                if let Some(inst) = reg.get_mut(&derived) {
                    inst.users = inst.users.saturating_sub(1);
                }
                Err(with_direct(
                    CoreError::new(PROXY_DISCONNECT)
                        .with("reason", Value::from("已登记的代理实例连接失败")),
                    direct_error,
                ))
            }
        };
    }

    let args = spawn_args(launch_args, &derived);
    let mut cmd = Command::new(launch_file);
    cmd.args(&args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child = cmd.spawn().map_err(|e| {
        with_direct(
            CoreError::new(PROXY_LAUNCH_FAILED)
                .with("proxyPath", Value::from(launch_file.to_string()))
                .with("osError", Value::from(e.raw_os_error().unwrap_or(0))),
            direct_error,
        )
    })?;

    let deadline = Instant::now() + Duration::from_millis(launch_timeout_ms);
    loop {
        match try_connect(&derived, 0) {
            TryConnect::Connected(client) => {
                reg.insert(
                    derived.clone(),
                    Instance {
                        child: Some(child),
                        users: 1,
                    },
                );
                return Ok(ProxyLease {
                    client,
                    handle: 0,
                    size: 0,
                    pipe_name: derived,
                    owned: true,
                    released: false,
                });
            }
            TryConnect::Rejected(e) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(with_direct(e, direct_error));
            }
            TryConnect::Unavailable => {}
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err(with_direct(
                CoreError::new(PROXY_LAUNCH_TIMEOUT)
                    .with("proxyPath", Value::from(launch_file.to_string()))
                    .with("timeoutMs", Value::from(launch_timeout_ms)),
                direct_error,
            ));
        }
        std::thread::sleep(Duration::from_millis(POLL_MS));
    }
}

/// 为已取得的 lease 执行 open，并把错误附上直开失败证据。
pub fn open_file(
    lease: &mut ProxyLease,
    path: &str,
    direct_error: Option<&CoreError>,
) -> CoreResult<()> {
    match lease.client.open(path) {
        Ok((h, size)) => {
            lease.handle = h;
            lease.size = size;
            Ok(())
        }
        Err(e) => Err(with_direct(e, direct_error)),
    }
}

pub fn with_direct(mut err: CoreError, direct: Option<&CoreError>) -> CoreError {
    if let Some(d) = direct {
        err.params.insert(
            DIRECT_OPEN_ERROR_KEY.to_string(),
            d.to_json()["params"].clone(),
        );
    }
    err
}

/// spool：逐块读到 EOF，内容落盘到 `dest`；实收与 open.size 不符按断线报。
pub fn spool(
    lease: &mut ProxyLease,
    dest: &Path,
    mut on_progress: impl FnMut(u64),
    cancelled: impl Fn() -> bool,
) -> CoreResult<u64> {
    let expected = lease.size;
    let mut out = File::create(dest).map_err(|e| {
        CoreError::new(crate::error::IO_ERROR)
            .with("path", Value::from(dest.to_string_lossy().to_string()))
            .with("osError", Value::from(e.raw_os_error().unwrap_or(0)))
    })?;
    let mut received = 0u64;
    loop {
        if cancelled() {
            return Err(CoreError::new(crate::error::CLOSED));
        }
        let chunk = lease.client.read(lease.handle, received, CHUNK_BYTES)?;
        if chunk.is_empty() {
            break;
        }
        out.write_all(&chunk).map_err(|e| {
            CoreError::new(crate::error::IO_ERROR)
                .with("path", Value::from(dest.to_string_lossy().to_string()))
                .with("osError", Value::from(e.raw_os_error().unwrap_or(0)))
        })?;
        received += chunk.len() as u64;
        on_progress(received);
    }
    out.flush().ok();
    if received != expected {
        return Err(CoreError::new(PROXY_DISCONNECT).with(
            "reason",
            Value::from(format!("实收 {received} 字节，期望 {expected} 字节")),
        ));
    }
    Ok(received)
}

/// 进程级收尾：结束全部 core 拉起的代理并清空登记（幂等，不碰外部代理）。
pub fn shutdown() {
    let mut reg = registry().lock().unwrap();
    for (_, mut inst) in reg.drain() {
        if let Some(mut child) = inst.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[cfg(test)]
pub fn registry_len() -> usize {
    registry().lock().unwrap().len()
}

#[cfg(test)]
pub fn registry_user_count(pipe_name: &str) -> Option<usize> {
    registry().lock().unwrap().get(pipe_name).map(|i| i.users)
}

/// 测试用：当前登记表中的实际管道名。
#[cfg(test)]
pub fn registry_keys() -> Vec<String> {
    registry().lock().unwrap().keys().cloned().collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use crate::proxy_support::{test_proxy_exe, TestProxyServer};
    use crate::testutil::{rand_suffix, unique_pipe};

    fn record_path(tag: &str) -> std::path::PathBuf {
        std::env::temp_dir().join(format!("logcore-spawn-{}-{tag}.txt", std::process::id()))
    }

    #[test]
    fn proxy_spawn_uses_derived_pipe_name() {
        shutdown();
        let pipe = unique_pipe("derived");
        let record = record_path("derived");
        let _ = std::fs::remove_file(&record);
        let args = vec![
            "--record".to_string(),
            record.to_string_lossy().to_string(),
            "--serve".to_string(),
            std::env::temp_dir().to_string_lossy().to_string(),
        ];
        let lease = acquire(&pipe, &test_proxy_exe(), &args, 5000, None).expect("拉起代理");
        drop(lease);

        let text = std::fs::read_to_string(&record).expect("代理应写出 argv 记录");
        let expected = format!("{pipe}-{}", std::process::id());
        assert!(
            text.contains(&expected),
            "派生名应为 {expected}，实际记录：{text}"
        );
        assert!(!text.contains(&format!("{pipe}\n")), "不得以基础名拉起");
        shutdown();
    }

    #[test]
    fn proxy_instance_reused_per_pipe() {
        shutdown();
        let pipe = unique_pipe("reuse");
        let exe = test_proxy_exe();
        let args = vec![
            "--serve".to_string(),
            std::env::temp_dir().to_string_lossy().to_string(),
        ];
        let a = acquire(&pipe, &exe, &args, 5000, None).unwrap();
        let b = acquire(&pipe, &exe, &args, 5000, None).unwrap();
        let derived = format!("{pipe}-{}", std::process::id());
        assert_eq!(registry_user_count(&derived), Some(2));
        assert_eq!(registry_len(), 1);
        drop(a);
        assert_eq!(
            registry_user_count(&derived),
            Some(1),
            "关一个源不应结束代理"
        );
        drop(b);
        assert_eq!(registry_len(), 0, "最后一个使用者关闭后结束");
        shutdown();
    }

    #[test]
    fn proxy_launch_single_flight() {
        shutdown();
        let pipe = unique_pipe("singleflight");
        let exe = test_proxy_exe();
        let args = vec![
            "--serve".to_string(),
            std::env::temp_dir().to_string_lossy().to_string(),
        ];
        // 租约留在各线程里直到断言完成；用通道替代栅栏，任一线程失败也能先收口再断言，不会死等。
        let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<(), String>>();
        let (release_tx, release_rx) = std::sync::mpsc::channel::<()>();
        let release_rx = std::sync::Arc::new(std::sync::Mutex::new(release_rx));
        let mut handles = Vec::new();
        for _ in 0..4 {
            let pipe = pipe.clone();
            let exe = exe.clone();
            let args = args.clone();
            let ready_tx = ready_tx.clone();
            let release_rx = release_rx.clone();
            handles.push(std::thread::spawn(move || match acquire(&pipe, &exe, &args, 5000, None) {
                Ok(lease) => {
                    let _ = ready_tx.send(Ok(()));
                    let _ = release_rx.lock().unwrap().recv();
                    drop(lease);
                }
                Err(err) => {
                    let _ = ready_tx.send(Err(format!("{err:?}")));
                }
            }));
        }
        drop(ready_tx);
        let results: Vec<Result<(), String>> =
            (0..4).filter_map(|_| ready_rx.recv().ok()).collect();
        let derived = format!("{pipe}-{}", std::process::id());
        let users = registry_user_count(&derived);
        let len = registry_len();
        for _ in 0..4 {
            let _ = release_tx.send(());
        }
        for h in handles {
            h.join().unwrap();
        }
        let failures: Vec<_> = results.iter().filter_map(|r| r.as_ref().err()).collect();
        assert!(failures.is_empty(), "并发拉起失败：{failures:?}");
        assert_eq!(results.len(), 4, "有线程未上报租约结果");
        assert_eq!(users, Some(4), "并发需要只拉起一个实例");
        assert_eq!(len, 1);
        shutdown();
    }

    #[test]
    fn proxy_launch_failure_classified() {
        shutdown();
        let pipe = unique_pipe("nofile");
        let missing = std::env::temp_dir().join("logcore-no-such-proxy.exe");
        let err = acquire(&pipe, &missing.to_string_lossy(), &[], 1000, None).err().unwrap();
        assert_eq!(err.code, PROXY_LAUNCH_FAILED);
        assert_eq!(
            err.params.get("proxyPath").and_then(|v| v.as_str()),
            Some(missing.to_string_lossy().as_ref())
        );
        assert!(err.params.get("osError").and_then(|v| v.as_i64()).is_some());
    }

    #[test]
    fn proxy_launch_timeout_classified() {
        shutdown();
        let pipe = unique_pipe("hold");
        let exe = test_proxy_exe();
        let args = vec!["--hold-ms".to_string(), "5000".to_string()];
        let started = Instant::now();
        let err = acquire(&pipe, &exe, &args, 300, None).err().unwrap();
        assert_eq!(err.code, PROXY_LAUNCH_TIMEOUT);
        assert_eq!(
            err.params.get("timeoutMs").and_then(|v| v.as_u64()),
            Some(300)
        );
        assert!(started.elapsed() < Duration::from_secs(4));
        assert_eq!(registry_len(), 0, "超时实例不登记");
    }

    #[test]
    fn proxy_proto_mismatch_rejected_without_spawn() {
        shutdown();
        let pipe = unique_pipe("proto");
        let server = TestProxyServer::start_with_proto(&pipe, 2);
        let exe = test_proxy_exe();
        let err = acquire(&pipe, &exe, &[], 1000, None).err().unwrap();
        assert_eq!(err.code, PROXY_DISCONNECT);
        assert_eq!(
            err.params.get("reason").and_then(|v| v.as_str()),
            Some("代理协议版本不匹配")
        );
        assert_eq!(registry_len(), 0, "握手失败不得覆盖对方进程");
        assert!(server.is_alive());
        shutdown();
    }

    #[test]
    fn proxy_base_name_external_not_owned() {
        shutdown();
        let pipe = unique_pipe("external");
        let server = TestProxyServer::start(&pipe);
        let exe = test_proxy_exe();
        let lease = acquire(&pipe, &exe, &[], 1000, None).expect("应连上外部代理");
        assert_eq!(registry_len(), 0, "外部代理不登记");
        drop(lease);
        assert!(server.is_alive(), "外部代理不应被 core 结束");
        shutdown();
        assert!(server.is_alive(), "shutdown 不碰外部代理");
    }

    #[test]
    fn proxy_shutdown_owned_only() {
        shutdown();
        let external_pipe = unique_pipe("ext-own");
        let server = TestProxyServer::start(&external_pipe);
        let owned_pipe = unique_pipe("owned");
        let exe = test_proxy_exe();
        let args = vec![
            "--serve".to_string(),
            std::env::temp_dir().to_string_lossy().to_string(),
        ];
        let _owned = acquire(&owned_pipe, &exe, &args, 5000, None).unwrap();
        let _ext = acquire(&external_pipe, &exe, &[], 1000, None).unwrap();
        assert_eq!(registry_len(), 1);
        shutdown();
        assert_eq!(registry_len(), 0);
        assert!(server.is_alive(), "外部代理必须存活");
        shutdown(); // 幂等
    }

    #[test]
    fn proxy_spool_receives_bytes() {
        shutdown();
        let pipe = unique_pipe("spool");
        let root = std::env::temp_dir().join(format!("logcore-spool-{}", rand_suffix()));
        std::fs::create_dir_all(&root).unwrap();
        let source = root.join("content.log");
        let content: Vec<u8> = (0..200_000u32).map(|i| (i % 251) as u8).collect();
        std::fs::write(&source, &content).unwrap();
        let args = vec![
            "--serve".to_string(),
            root.to_string_lossy().to_string(),
        ];
        let mut lease = acquire(&pipe, &test_proxy_exe(), &args, 5000, None).unwrap();
        open_file(&mut lease, &source.to_string_lossy(), None).unwrap();
        assert_eq!(lease.size, content.len() as u64);
        let dest = root.join("out.bin");
        let mut last = 0;
        let written = spool(&mut lease, &dest, |n| last = n, || false).unwrap();
        assert_eq!(written, content.len() as u64);
        assert_eq!(last, written);
        assert_eq!(std::fs::read(&dest).unwrap(), content);
        drop(lease);
        shutdown();
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn proxy_shutdown_interrupts_active_spool() {
        shutdown();
        let pipe = unique_pipe("interrupt");
        let root = std::env::temp_dir().join(format!("logcore-int-{}", rand_suffix()));
        std::fs::create_dir_all(&root).unwrap();
        let source = root.join("big.log");
        std::fs::write(&source, vec![b'a'; 4 * 1024 * 1024]).unwrap();
        let args = vec![
            "--serve".to_string(),
            root.to_string_lossy().to_string(),
            "--pace-ms".to_string(),
            "400".to_string(),
        ];
        let mut lease = acquire(&pipe, &test_proxy_exe(), &args, 5000, None).unwrap();
        open_file(&mut lease, &source.to_string_lossy(), None).unwrap();
        let dest = root.join("spool.bin");

        let spooler = std::thread::spawn(move || {
            let err = spool(&mut lease, &dest, |_| {}, || false).unwrap_err();
            (err, lease)
        });
        std::thread::sleep(Duration::from_millis(100));
        shutdown();
        let (err, _lease) = spooler.join().unwrap();
        assert_eq!(err.code, PROXY_DISCONNECT, "中途断线应报代理断开");
        assert_eq!(registry_len(), 0);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn proxy_direct_error_attached() {
        let direct = CoreError::new(crate::error::DIRECT_OPEN_FAILED)
            .with("path", Value::from("C:\\a.log"))
            .with("osError", Value::from(5));
        let err = with_direct(disconnect_msg("x"), Some(&direct));
        assert_eq!(err.code, PROXY_DISCONNECT);
        assert_eq!(err.to_json()["params"]["directOpenError"]["osError"], 5);
        assert_eq!(err.to_json()["params"]["directOpenError"]["path"], "C:\\a.log");
        let _ = json!(null);
    }
}