//! 命名管道服务端（测试对端专用，不进入生产 FFI 面）。
//!
//! `logcore-test-proxy` 二进制与进程内 `TestProxyServer` 共用这套最小实现：
//! 字节模式、阻塞读写、每连接一线程。协议处理见 [`serve_test_protocol`]，
//! 与 `FakeProxyServer`（C#）同构：hello / open / read / close。

#![cfg(windows)]

use std::io::{Read, Seek, SeekFrom, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, HANDLE};
use windows_sys::Win32::Storage::FileSystem::{FlushFileBuffers, ReadFile, WriteFile};
use windows_sys::Win32::System::Pipes::{ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe};

const PIPE_ACCESS_DUPLEX: u32 = 0x0000_0003;
const PIPE_TYPE_BYTE: u32 = 0x0000_0000;
const PIPE_READMODE_BYTE: u32 = 0x0000_0000;
const PIPE_WAIT: u32 = 0x0000_0000;
const PIPE_UNLIMITED_INSTANCES: u32 = 255;
const ERROR_PIPE_CONNECTED: u32 = 535;
const ERROR_BROKEN_PIPE: u32 = 109;
const INVALID_HANDLE_VALUE: HANDLE = -1isize as HANDLE;

pub struct PipeServer {
    handle: HANDLE,
}

// 句柄是内核对象标识；阻塞调用本身线程安全，跨线程移动不共享可变别名。
unsafe impl Send for PipeServer {}
unsafe impl Sync for PipeServer {}

impl PipeServer {
    /// 创建一个实例并进入监听（不阻塞）。失败返回 Win32 错误码。
    pub fn create(pipe_name: &str) -> std::io::Result<Self> {
        let mut name: Vec<u16> = format!("\\\\.\\pipe\\{pipe_name}")
            .encode_utf16()
            .collect();
        name.push(0);
        let handle = unsafe {
            CreateNamedPipeW(
                name.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                64 * 1024,
                64 * 1024,
                0,
                std::ptr::null(),
            )
        };
        if handle == INVALID_HANDLE_VALUE || handle.is_null() {
            return Err(std::io::Error::last_os_error());
        }
        Ok(Self { handle })
    }

    /// 阻塞等待客户端连接。
    pub fn connect(&mut self) -> std::io::Result<()> {
        let ok = unsafe { ConnectNamedPipe(self.handle, std::ptr::null_mut()) };
        if ok != 0 {
            return Ok(());
        }
        let err = unsafe { GetLastError() };
        if err == ERROR_PIPE_CONNECTED {
            Ok(())
        } else {
            Err(std::io::Error::from_raw_os_error(err as i32))
        }
    }

    pub fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let mut read: u32 = 0;
        let ok = unsafe {
            ReadFile(
                self.handle,
                buf.as_mut_ptr(),
                buf.len() as u32,
                &mut read,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            let err = unsafe { GetLastError() };
            if err == ERROR_BROKEN_PIPE {
                return Ok(0);
            }
            return Err(std::io::Error::from_raw_os_error(err as i32));
        }
        Ok(read as usize)
    }

    pub fn write_all(&mut self, bytes: &[u8]) -> std::io::Result<()> {
        let mut written_total = 0usize;
        while written_total < bytes.len() {
            let mut written: u32 = 0;
            let ok = unsafe {
                WriteFile(
                    self.handle,
                    bytes[written_total..].as_ptr(),
                    (bytes.len() - written_total) as u32,
                    &mut written,
                    std::ptr::null_mut(),
                )
            };
            if ok == 0 {
                return Err(std::io::Error::last_os_error());
            }
            if written == 0 {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::WriteZero,
                    "管道写入 0 字节",
                ));
            }
            written_total += written as usize;
        }
        Ok(())
    }

    pub fn flush(&mut self) -> std::io::Result<()> {
        let ok = unsafe { FlushFileBuffers(self.handle) };
        if ok == 0 {
            return Err(std::io::Error::last_os_error());
        }
        Ok(())
    }
}

impl Drop for PipeServer {
    fn drop(&mut self) {
        unsafe {
            DisconnectNamedPipe(self.handle);
            CloseHandle(self.handle);
        }
    }
}

impl Read for PipeServer {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        PipeServer::read(self, buf)
    }
}

impl Write for PipeServer {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.write_all(buf)?;
        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        PipeServer::flush(self)
    }
}

/// 测试代理协议行为参数。
#[derive(Clone, Debug, Default)]
pub struct ServeOptions {
    /// 文件根目录：请求路径打不开时按 basename 回退到此目录（模拟「直开失败但代理可读」）。
    pub serve_dir: Option<String>,
    /// 每次 read 应答前的延迟（让 spool 可被中途打断）。
    pub pace_ms: u64,
    /// hello 自描述的协议版本。
    pub proto: i32,
}

/// 接受循环：每连接一线程，实例耗尽后自动补齐；`stop` 置位后不再接受新连接。
pub fn spawn_listener(pipe_name: &str, opts: Arc<ServeOptions>) -> (Arc<AtomicBool>, thread::JoinHandle<()>) {
    let stop = Arc::new(AtomicBool::new(false));
    let stop_flag = stop.clone();
    let pipe = pipe_name.to_string();
    let join = thread::spawn(move || {
        while !stop_flag.load(Ordering::SeqCst) {
            let mut server = match PipeServer::create(&pipe) {
                Ok(s) => s,
                Err(_) => {
                    thread::sleep(Duration::from_millis(20));
                    continue;
                }
            };
            if server.connect().is_err() {
                continue;
            }
            let conn_opts = opts.clone();
            thread::spawn(move || {
                let _ = serve_test_protocol(&mut server, &conn_opts);
            });
        }
    });
    (stop, join)
}

/// 协议 v1 服务端：逐行收控制消息，read 应答头后紧跟裸块。
pub fn serve_test_protocol(server: &mut PipeServer, opts: &ServeOptions) -> std::io::Result<()> {
    let mut reader = LineReader::default();
    let mut files: Vec<Option<std::fs::File>> = Vec::new();
    loop {
        let line = match reader.read_line(server)? {
            Some(l) => l,
            None => return Ok(()),
        };
        if line.trim().is_empty() {
            continue;
        }
        let msg: serde_json::Value = match serde_json::from_str(&line) {
            Ok(v) => v,
            Err(_) => {
                write_json(server, serde_json::json!({"id":0,"ok":false,"err":"bad json"}))?;
                continue;
            }
        };
        let id = msg["id"].as_i64().unwrap_or(0);
        match msg["op"].as_str().unwrap_or("") {
            "hello" => {
                write_json(
                    server,
                    serde_json::json!({
                        "id": id, "ok": true,
                        "proxy": "logcore-test-proxy/0.1",
                        "proto": opts.proto,
                    }),
                )?;
            }
            "open" => {
                let requested = msg["path"].as_str().unwrap_or("");
                match open_for(requested, opts.serve_dir.as_deref()) {
                    Ok(fs) => {
                        let size = fs.metadata().map(|m| m.len()).unwrap_or(0);
                        files.push(Some(fs));
                        let handle = files.len() as i64;
                        write_json(
                            server,
                            serde_json::json!({"id": id, "ok": true, "h": handle, "size": size}),
                        )?;
                    }
                    Err(e) => {
                        write_json(
                            server,
                            serde_json::json!({"id": id, "ok": false, "err": e.to_string()}),
                        )?;
                    }
                }
            }
            "read" => {
                let h = msg["h"].as_i64().unwrap_or(-1);
                let off = msg["off"].as_u64().unwrap_or(0);
                let len = msg["len"].as_u64().unwrap_or(0).min(16 * 1024 * 1024);
                if opts.pace_ms > 0 {
                    thread::sleep(Duration::from_millis(opts.pace_ms));
                }
                let file = h
                    .checked_sub(1)
                    .and_then(|i| files.get_mut(i as usize))
                    .and_then(|slot| slot.as_mut());
                let Some(file) = file else {
                    write_json(
                        server,
                        serde_json::json!({"id": id, "ok": false, "err": "bad handle"}),
                    )?;
                    continue;
                };
                let size = file.metadata().map(|m| m.len()).unwrap_or(0);
                let want = len.min(size.saturating_sub(off)) as usize;
                let mut buf = vec![0u8; want];
                if want > 0 {
                    file.seek(SeekFrom::Start(off))?;
                    file.read_exact(&mut buf)?;
                }
                write_json(server, serde_json::json!({"id": id, "ok": true, "n": want}))?;
                if want > 0 {
                    server.write_all(&buf)?;
                    server.flush()?;
                }
            }
            "close" => {
                let h = msg["h"].as_i64().unwrap_or(-1);
                if let Some(i) = h.checked_sub(1) {
                    if let Some(slot) = files.get_mut(i as usize) {
                        *slot = None;
                    }
                }
                write_json(server, serde_json::json!({"id": id, "ok": true}))?;
            }
            other => {
                write_json(
                    server,
                    serde_json::json!({"id": id, "ok": false, "err": format!("unknown op: {other}")}),
                )?;
            }
        }
    }
}

fn open_for(requested: &str, serve_dir: Option<&str>) -> std::io::Result<std::fs::File> {
    if let Ok(f) = std::fs::File::open(requested) {
        return Ok(f);
    }
    let Some(root) = serve_dir else {
        return std::fs::File::open(requested);
    };
    let name = std::path::Path::new(requested)
        .file_name()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "spool.log".to_string());
    std::fs::File::open(std::path::Path::new(root).join(name))
}

fn write_json(server: &mut PipeServer, value: serde_json::Value) -> std::io::Result<()> {
    let mut bytes = serde_json::to_vec(&value).unwrap_or_default();
    bytes.push(b'\n');
    server.write_all(&bytes)?;
    server.flush()
}

#[derive(Default)]
struct LineReader {
    buf: Vec<u8>,
}

impl LineReader {
    fn read_line(&mut self, server: &mut PipeServer) -> std::io::Result<Option<String>> {
        loop {
            if let Some(pos) = self.buf.iter().position(|b| *b == b'\n') {
                let line: Vec<u8> = self.buf.drain(..=pos).collect();
                return Ok(Some(
                    String::from_utf8_lossy(&line[..line.len() - 1]).to_string(),
                ));
            }
            let mut chunk = [0u8; 8192];
            let n = PipeServer::read(server, &mut chunk)?;
            if n == 0 {
                return Ok(None);
            }
            self.buf.extend_from_slice(&chunk[..n]);
        }
    }
}