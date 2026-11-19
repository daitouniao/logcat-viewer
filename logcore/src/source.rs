//! 源获取：plan / commit / feed / stop / close 与事件、进度。
//!
//! commit 受理即返（D24）：句柄先进入 `Committing` 并产出描述骨架，装载在内部线程
//! 继续，完成/失败经事件上报；采集源受理即进入 `Running`。关闭丢弃未消化队列并
//! 释放源资源，句柄保留至 `free_handle`（D12 / D13）。

use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::SystemTime;

use serde_json::{json, Value};

use crate::descriptor::{Descriptor, LoadMode, Route};
use crate::error::{
    CoreError, CoreResult, CLOSED, DIRECT_OPEN_FAILED, FAILED, INTERNAL, INVALID_STATE, IO_ERROR,
    NOT_FOUND, PROXY_DISCONNECT, STOPPED, UNSUPPORTED_FORMAT,
};
use crate::events::{Event, EventQueue, Progress, Stage};
use crate::format;
use crate::identity::Identity;
use crate::locks;
use crate::parse::Indexer;
use crate::probe;
use crate::proxy::{self, ProxyLease};
use crate::request::{self, Kind, Mode, Request};
use crate::route;
use crate::sidecar::{CacheStore, Payload};
use crate::state::{self, Op, State};

pub const MAX_FEED_LINES: usize = 65536;

type Batch = Vec<Vec<u8>>;

struct Shared {
    state: AtomicU8,
    progress: Progress,
    events: EventQueue,
    cancelled: AtomicBool,
    discard: AtomicBool,
    rows: AtomicU64,
    doc_size: AtomicU64,
    queued_rows: AtomicU64,
    consumer_done: Mutex<bool>,
    consumer_cv: Condvar,
    stop_done: Mutex<bool>,
    stop_outcome: Mutex<i32>,
    stop_cv: Condvar,
    /// 「入排空快照」与「消化批次计数」互斥，保证排空进度结束时 value == total。
    drain_sync: Mutex<()>,
    #[cfg(test)]
    fail_writes: AtomicBool,
    #[cfg(test)]
    slow_consume: AtomicBool,
    #[cfg(test)]
    slow_parse: AtomicBool,
    #[cfg(test)]
    loader_gate: Mutex<bool>,
    #[cfg(test)]
    loader_cv: Condvar,
}

impl Shared {
    fn new() -> Self {
        Self {
            state: AtomicU8::new(State::PlanReady as u8),
            progress: Progress::default(),
            events: EventQueue::default(),
            cancelled: AtomicBool::new(false),
            discard: AtomicBool::new(false),
            rows: AtomicU64::new(0),
            doc_size: AtomicU64::new(0),
            queued_rows: AtomicU64::new(0),
            consumer_done: Mutex::new(false),
            consumer_cv: Condvar::new(),
            stop_done: Mutex::new(false),
            stop_outcome: Mutex::new(crate::error::OK),
            stop_cv: Condvar::new(),
            drain_sync: Mutex::new(()),
            #[cfg(test)]
            fail_writes: AtomicBool::new(false),
            #[cfg(test)]
            slow_consume: AtomicBool::new(false),
            #[cfg(test)]
            slow_parse: AtomicBool::new(false),
            #[cfg(test)]
            loader_gate: Mutex::new(true),
            #[cfg(test)]
            loader_cv: Condvar::new(),
        }
    }

    fn consumer_finished(&self) {
        *self.consumer_done.lock().unwrap() = true;
        self.consumer_cv.notify_all();
    }

    fn wait_consumer(&self) {
        let mut done = self.consumer_done.lock().unwrap();
        while !*done {
            done = self.consumer_cv.wait(done).unwrap();
        }
    }

    /// 首次 stop 的结果广播给排空期间的其他 stop 调用（D23 幂等等待同一结果）。
    fn publish_stop(&self, code: i32) {
        *self.stop_outcome.lock().unwrap() = code;
        *self.stop_done.lock().unwrap() = true;
        self.stop_cv.notify_all();
    }

    fn wait_stop(&self) -> CoreResult<()> {
        let mut done = self.stop_done.lock().unwrap();
        while !*done {
            done = self.stop_cv.wait(done).unwrap();
        }
        drop(done);
        match *self.stop_outcome.lock().unwrap() {
            FAILED => Err(CoreError::new(FAILED)),
            CLOSED => Err(CoreError::new(CLOSED)),
            _ => Ok(()),
        }
    }

    /// 测试闸门：拦住装载线程进入首阶段，便于断言「commit 受理本身不发事件」。
    #[cfg(test)]
    fn hold_loader(&self) {
        *self.loader_gate.lock().unwrap() = false;
    }

    #[cfg(test)]
    fn release_loader(&self) {
        *self.loader_gate.lock().unwrap() = true;
        self.loader_cv.notify_all();
    }

    fn wait_loader_gate(&self) {
        #[cfg(test)]
        {
            let mut open = self.loader_gate.lock().unwrap();
            while !*open && !self.cancelled.load(Ordering::SeqCst) {
                open = self.loader_cv.wait(open).unwrap();
            }
        }
    }

    #[cfg(test)]
    fn set_fail_writes(&self, on: bool) {
        self.fail_writes.store(on, Ordering::SeqCst);
    }

    #[cfg(test)]
    fn set_slow_consume(&self, on: bool) {
        self.slow_consume.store(on, Ordering::SeqCst);
    }

    #[cfg(test)]
    fn set_slow_parse(&self, on: bool) {
        self.slow_parse.store(on, Ordering::SeqCst);
    }
}

struct Inner {
    request: Request,
    route: Route,
    plan: Value,
    plan_identity: Option<Identity>,
    direct_error: Option<CoreError>,
    lease: Option<ProxyLease>,
    cache: Option<Arc<CacheStore>>,
    cache_hit: Option<Payload>,
    cache_marked: bool,
    descriptor: Option<Descriptor>,
    payload: Option<Payload>,
    spool_dir: Option<PathBuf>,
    spool_marker: Option<File>,
    capture_path: Option<PathBuf>,
    capture_marker: Option<File>,
    sender: Option<Sender<Batch>>,
    consumer: Option<JoinHandle<()>>,
    loader: Option<JoinHandle<()>>,
    plan_valid: bool,
}

/// 一个活动源句柄。`shared` 供装载/消化线程与轮询路径共享；
/// `inner` 保护资源；`lifecycle` 串行化 commit / cancel / stop / close。
pub struct SourceHandle {
    shared: Arc<Shared>,
    inner: Arc<Mutex<Inner>>,
    lifecycle: Mutex<()>,
}

fn state_from(v: u8) -> State {
    match v {
        0 => State::PlanReady,
        1 => State::Committing,
        2 => State::Loaded,
        3 => State::Running,
        4 => State::Stopping,
        5 => State::Stopped,
        6 => State::Failed,
        _ => State::Closed,
    }
}

fn enter_stage(shared: &Shared, stage: Stage, total: Option<u64>) {
    shared.progress.set_stage(stage, total);
    shared.events.push(Event::Stage {
        stage,
        value: 0,
        total,
    });
}

fn check_cancel(shared: &Shared) -> CoreResult<()> {
    if shared.cancelled.load(Ordering::SeqCst) {
        Err(CoreError::new(CLOSED))
    } else {
        Ok(())
    }
}

impl SourceHandle {
    pub fn state(&self) -> State {
        state_from(self.shared.state.load(Ordering::SeqCst))
    }

    fn set_state(&self, state: State) {
        self.shared.state.store(state as u8, Ordering::SeqCst);
    }

    pub fn plan_json(&self) -> Value {
        self.inner.lock().unwrap().plan.clone()
    }

    /// 两段式第一段：只判定不装载。返回句柄，commit 复用本句柄。
    pub fn plan(request_json: &str) -> CoreResult<Box<SourceHandle>> {
        let req = request::parse(request_json)?;
        let mut inner = new_inner(req.clone());
        if req.kind == Kind::Device {
            inner.route = Route::DeviceStream;
            inner.plan = json!({ "route": "DeviceStream", "estimatedSize": Value::Null });
            return Ok(Box::new(SourceHandle::new(inner)));
        }

        let display = req.path.clone().unwrap();
        let open_path = open_path_for(&display);

        match fs::metadata(&open_path) {
            Ok(_) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                return Err(CoreError::new(NOT_FOUND).with("path", Value::from(display)))
            }
            Err(e) => inner.direct_error = Some(direct_error(&display, &e)),
        }

        let mut direct_ok = false;
        let mut looks_text = false;

        if req.mode != Mode::On {
            match File::open(&open_path) {
                Ok(mut f) => {
                    direct_ok = true;
                    match probe::read_window(&mut f, req.options.probe_window_bytes) {
                        Ok(sample) => {
                            if let Some(fmt) = format::detect_unsupported_format(&sample) {
                                return Err(reject_unsupported(fmt));
                            }
                            if !probe::sample_is_text(&sample, req.options.probe_threshold) {
                                return Err(reject_unsupported("Binary"));
                            }
                            looks_text = true;
                        }
                        // 读窗口失败不作为非文本证据：Auto 回退代理、Off 继续直读
                        Err(_) => {}
                    }
                }
                Err(e) => inner.direct_error = Some(direct_error(&display, &e)),
            }
        }

        if route::should_use_proxy(req.mode, direct_ok, looks_text) {
            let cfg = req.proxy.as_ref().expect("文件源请求必带代理配置");
            let mut lease = proxy::acquire(
                &cfg.pipe_name,
                &cfg.launch_file,
                &cfg.launch_args,
                cfg.launch_timeout_ms,
                inner.direct_error.as_ref(),
            )?;
            if let Err(e) = proxy::open_file(&mut lease, &display, inner.direct_error.as_ref()) {
                lease.release();
                return Err(e);
            }
            let size = lease.size;
            inner.lease = Some(lease);
            inner.route = Route::ProxySpool;
            inner.plan = json!({ "route": "ProxySpool", "estimatedSize": size });
        } else {
            if !direct_ok {
                return Err(inner
                    .direct_error
                    .take()
                    .unwrap_or_else(|| CoreError::new(DIRECT_OPEN_FAILED).with("path", Value::from(display))));
            }
            inner.route = Route::LocalDirect;
            let identity = Identity::capture(Path::new(&open_path))
                .map_err(|e| direct_error(&display, &e))?;
            let size = identity.size;
            inner.plan_identity = Some(identity.clone());
            if let Some(cfg) = &req.options.sidecar {
                let store = Arc::new(CacheStore::new(
                    cfg.dir.clone(),
                    cfg.entry_limit_bytes,
                    cfg.dir_limit_bytes,
                ));
                inner.cache_hit = store.probe(&display, &identity);
                inner.cache_marked = inner.cache_hit.is_some();
                inner.cache = Some(store);
            }
            inner.plan = json!({ "route": "LocalDirect", "estimatedSize": size });
        }

        Ok(Box::new(SourceHandle::new(inner)))
    }

    fn new(inner: Inner) -> Self {
        Self {
            shared: Arc::new(Shared::new()),
            inner: Arc::new(Mutex::new(inner)),
            lifecycle: Mutex::new(()),
        }
    }

    /// 两段式第二段：受理后立即返回，装载在内部线程继续（D24）。
    pub fn commit(&self) -> CoreResult<()> {
        let _life = self.lifecycle.lock().unwrap();
        state::check(self.state(), Op::Commit)?;
        let mut inner = self.inner.lock().unwrap();
        if !inner.plan_valid {
            return Err(CoreError::new(INVALID_STATE));
        }

        if inner.route == Route::ProxySpool {
            let alive = match inner.lease.as_mut() {
                Some(lease) if lease.handle != 0 => {
                    let handle = lease.handle;
                    lease.client().probe_alive(handle).is_ok()
                }
                _ => false,
            };
            if !alive {
                if let Some(mut lease) = inner.lease.take() {
                    lease.release();
                }
                inner.plan_valid = false;
                return Err(CoreError::new(PROXY_DISCONNECT)
                    .with("reason", Value::from("planConnectionLost")));
            }
        }
        inner.plan_valid = false;

        match inner.route {
            Route::DeviceStream => self.accept_device(&mut inner),
            _ => self.accept_file(&mut inner),
        }
        Ok(())
    }

    fn accept_file(&self, inner: &mut Inner) {
        let req = inner.request.clone();
        let display = req.path.clone().unwrap();
        let open_path = open_path_for(&display);
        if inner.route == Route::ProxySpool {
            match reserve_spool(&req.options.temp_dir, &display) {
                Ok((dir, marker, dest)) => {
                    let lease_id = dir.file_name().map(|s| s.to_string_lossy().to_string());
                    inner.descriptor = Some(Descriptor::skeleton(
                        Route::ProxySpool,
                        None,
                        LoadMode::Progressive,
                        Some(display),
                        dest.to_string_lossy().to_string(),
                        lease_id,
                    ));
                    inner.spool_dir = Some(dir);
                    inner.spool_marker = Some(marker);
                }
                Err(e) => {
                    let planned = planned_spool_path(&req.options.temp_dir, &display);
                    let lease_id = planned
                        .parent()
                        .and_then(|p| p.file_name())
                        .map(|s| s.to_string_lossy().to_string());
                    inner.descriptor = Some(Descriptor::skeleton(
                        Route::ProxySpool,
                        None,
                        LoadMode::Progressive,
                        Some(display),
                        planned.to_string_lossy().to_string(),
                        lease_id,
                    ));
                    self.set_state(State::Failed);
                    self.shared.events.push(Event::Error(io_error(
                        req.options.temp_dir.to_string_lossy().to_string(),
                        &e,
                    )));
                    return;
                }
            }
        } else {
            inner.descriptor = Some(Descriptor::skeleton(
                Route::LocalDirect,
                inner.plan_identity.clone(),
                if inner.cache_hit.is_some() {
                    LoadMode::OneShot
                } else {
                    LoadMode::Progressive
                },
                Some(display),
                open_path,
                None,
            ));
        }
        self.shared.progress.set_stage(Stage::Opening, None);
        self.set_state(State::Committing);
        let shared = self.shared.clone();
        let handle = self.inner.clone();
        inner.loader = Some(std::thread::spawn(move || run_loader(handle, shared)));
    }

    fn accept_device(&self, inner: &mut Inner) {
        let temp = inner.request.options.temp_dir.clone();
        let planned = planned_capture_path(&temp);
        let planned_lease = planned.file_stem().map(|s| s.to_string_lossy().to_string());
        inner.descriptor = Some(Descriptor::skeleton(
            Route::DeviceStream,
            None,
            LoadMode::Progressive,
            None,
            planned.to_string_lossy().to_string(),
            planned_lease,
        ));
        // 受理本身不发事件（D22 / D24）：只落进度快照。
        self.shared.progress.set_stage(Stage::Capturing, None);

        match create_capture(&temp) {
            Ok((path, marker, file)) => {
                if let Some(desc) = inner.descriptor.as_mut() {
                    desc.source_path = path.to_string_lossy().to_string();
                    desc.lease_id = path.file_stem().map(|s| s.to_string_lossy().to_string());
                }
                let (tx, rx) = mpsc::channel::<Batch>();
                let consumer = spawn_consumer(rx, file, path.clone(), self.shared.clone());
                inner.capture_path = Some(path);
                inner.capture_marker = Some(marker);
                inner.sender = Some(tx);
                inner.consumer = Some(consumer);
                self.set_state(State::Running);
            }
            Err(e) => {
                self.set_state(State::Failed);
                self.shared
                    .events
                    .push(Event::Error(io_error(temp.to_string_lossy().to_string(), &e)));
            }
        }
    }

    /// 采集推入：入队即返；排空期间返回已停止；单批超过上限整批被拒。
    pub fn feed(&self, lines: Vec<Vec<u8>>) -> CoreResult<()> {
        // 参数形状错误与句柄状态无关（D15）：先判整批上限。
        if lines.len() > MAX_FEED_LINES {
            return Err(CoreError::invalid_arg("lines"));
        }
        state::check(self.state(), Op::Feed)?;
        if lines.is_empty() {
            return Ok(());
        }
        let n = lines.len() as u64;
        let inner = self.inner.lock().unwrap();
        let Some(sender) = inner.sender.as_ref() else {
            // stop / close 抢在状态检查之后取走发送端：按当前状态如实回码。
            drop(inner);
            return Err(self.current_state_error());
        };
        self.shared.queued_rows.fetch_add(n, Ordering::SeqCst);
        if sender.send(lines).is_err() {
            self.shared.queued_rows.fetch_sub(n, Ordering::SeqCst);
            return Err(self.current_state_error());
        }
        Ok(())
    }

    fn current_state_error(&self) -> CoreError {
        match self.state() {
            State::Stopping | State::Stopped => CoreError::new(STOPPED),
            State::Failed => CoreError::new(FAILED),
            State::Closed => CoreError::new(CLOSED),
            _ => CoreError::new(INVALID_STATE),
        }
    }

    /// 停止：排空队列、刷盘、发 stopped；句柄与采集文件保留。
    pub fn stop(&self) -> CoreResult<()> {
        let life = self.lifecycle.lock().unwrap();
        state::check(self.state(), Op::Stop)?;
        match self.state() {
            State::Stopped => return Ok(()),
            State::Failed => return Err(CoreError::new(FAILED)),
            State::Stopping => {
                drop(life);
                return self.shared.wait_stop();
            }
            State::Running => {
                self.set_state(State::Stopping);
                let guard = self.shared.drain_sync.lock().unwrap();
                let pending = self.shared.queued_rows.load(Ordering::SeqCst);
                enter_stage(&self.shared, Stage::Draining, Some(pending));
                drop(guard);
                self.inner.lock().unwrap().sender.take();
            }
            _ => unreachable!("stop 状态已由矩阵过滤"),
        }
        drop(life);

        self.shared.wait_consumer();

        let _life = self.lifecycle.lock().unwrap();
        let outcome = if self.shared.cancelled.load(Ordering::SeqCst) {
            // 排空期间被 close 中止：终态归 close，阻塞中的 stop 返回 CLOSED（D12）。
            Err(CoreError::new(CLOSED))
        } else {
            match self.state() {
                State::Closed => Err(CoreError::new(CLOSED)),
                State::Failed => Err(CoreError::new(FAILED)),
                State::Stopped => Ok(()),
                State::Stopping => {
                    self.set_state(State::Stopped);
                    self.shared.events.push(Event::Stopped);
                    Ok(())
                }
                _ => Ok(()),
            }
        };
        let code = match &outcome {
            Ok(()) => crate::error::OK,
            Err(e) => e.code,
        };
        self.shared.publish_stop(code);
        outcome
    }

    /// 关闭：丢弃未消化队列、释放源资源；不发终态事件，句柄保留至 `free_handle`。
    pub fn close(&self) -> CoreResult<()> {
        let (has_consumer, consumer, loader);
        {
            let _life = self.lifecycle.lock().unwrap();
            if self.state() == State::Closed {
                return Ok(());
            }
            self.shared.cancelled.store(true, Ordering::SeqCst);
            self.shared.discard.store(true, Ordering::SeqCst);
            let mut inner = self.inner.lock().unwrap();
            inner.sender.take();
            has_consumer = inner.consumer.is_some();
            consumer = inner.consumer.take();
            loader = inner.loader.take();
        }
        if has_consumer {
            self.shared.wait_consumer();
        }
        if let Some(join) = consumer {
            let _ = join.join();
        }
        if let Some(join) = loader {
            let _ = join.join();
        }

        let _life = self.lifecycle.lock().unwrap();
        let mut inner = self.inner.lock().unwrap();
        cleanup_resources(&mut inner);
        drop(inner);
        self.set_state(State::Closed);
        self.shared.events.clear();
        Ok(())
    }

    /// 取消：plan 阶段释放计划资源；commit 阶段中止装载并转已关闭。
    pub fn cancel(&self) -> CoreResult<()> {
        let loader;
        {
            let _life = self.lifecycle.lock().unwrap();
            state::check(self.state(), Op::Cancel)?;
            self.shared.cancelled.store(true, Ordering::SeqCst);
            loader = self.inner.lock().unwrap().loader.take();
        }
        if let Some(join) = loader {
            let _ = join.join();
        }
        let _life = self.lifecycle.lock().unwrap();
        let mut inner = self.inner.lock().unwrap();
        cleanup_resources(&mut inner);
        drop(inner);
        self.set_state(State::Closed);
        self.shared.events.clear();
        Ok(())
    }

    pub fn descriptor_json(&self) -> CoreResult<Value> {
        state::check(self.state(), Op::GetDescriptor)?;
        let inner = self.inner.lock().unwrap();
        let mut desc = inner
            .descriptor
            .clone()
            .ok_or_else(|| CoreError::new(INVALID_STATE))?;
        desc.row_count = self.shared.rows.load(Ordering::SeqCst);
        desc.doc_size = self.shared.doc_size.load(Ordering::SeqCst);
        Ok(desc.to_json())
    }

    pub fn poll_events(&self) -> CoreResult<Vec<Event>> {
        state::check(self.state(), Op::PollEvents)?;
        Ok(self.shared.events.drain_all())
    }

    pub fn progress_json(&self) -> CoreResult<Value> {
        state::check(self.state(), Op::GetProgress)?;
        Ok(self.shared.progress.to_json())
    }
}

#[cfg(test)]
impl SourceHandle {
    pub fn hold_loader(&self) {
        self.shared.hold_loader();
    }

    pub fn release_loader(&self) {
        self.shared.release_loader();
    }

    pub fn set_slow_parse(&self, on: bool) {
        self.shared.set_slow_parse(on);
    }

    pub fn set_slow_consume(&self, on: bool) {
        self.shared.set_slow_consume(on);
    }

    pub fn set_fail_writes(&self, on: bool) {
        self.shared.set_fail_writes(on);
    }

    pub fn events_len(&self) -> usize {
        self.shared.events.len()
    }
}

impl Drop for SourceHandle {
    fn drop(&mut self) {
        self.shared.cancelled.store(true, Ordering::SeqCst);
        self.shared.discard.store(true, Ordering::SeqCst);
        let (has_consumer, consumer, loader);
        {
            let mut inner = self.inner.lock().unwrap();
            inner.sender.take();
            has_consumer = inner.consumer.is_some();
            consumer = inner.consumer.take();
            loader = inner.loader.take();
        }
        if has_consumer {
            self.shared.wait_consumer();
        }
        if let Some(join) = consumer {
            let _ = join.join();
        }
        if let Some(join) = loader {
            let _ = join.join();
        }
        cleanup_resources(&mut self.inner.lock().unwrap());
    }
}

fn new_inner(req: Request) -> Inner {
    Inner {
        request: req,
        route: Route::LocalDirect,
        plan: Value::Null,
        plan_identity: None,
        direct_error: None,
        lease: None,
        cache: None,
        cache_hit: None,
        cache_marked: false,
        descriptor: None,
        payload: None,
        spool_dir: None,
        spool_marker: None,
        capture_path: None,
        capture_marker: None,
        sender: None,
        consumer: None,
        loader: None,
        plan_valid: true,
    }
}

fn cleanup_resources(inner: &mut Inner) {
    if let Some(mut lease) = inner.lease.take() {
        lease.release();
    }
    if let Some(dir) = inner.spool_dir.take() {
        inner.spool_marker.take();
        let _ = fs::remove_dir_all(&dir);
        let _ = fs::remove_file(locks::marker_path(&dir));
    }
    if let Some(path) = inner.capture_path.take() {
        inner.capture_marker.take();
        let _ = fs::remove_file(&path);
        let _ = fs::remove_file(locks::marker_path(&path));
    }
    if inner.cache_marked {
        if let (Some(store), Some(path)) = (inner.cache.clone(), inner.request.path.clone()) {
            store.unmark_in_use(&path);
        }
        inner.cache_marked = false;
    }
}

fn direct_error(display: &str, e: &std::io::Error) -> CoreError {
    CoreError::new(DIRECT_OPEN_FAILED)
        .with("path", Value::from(display))
        .with("osError", Value::from(e.raw_os_error().unwrap_or(0)))
}

fn reject_unsupported(format: &'static str) -> CoreError {
    CoreError::new(UNSUPPORTED_FORMAT).with("format", Value::from(format))
}

/// 仅超长绝对路径补长路径前缀（D14）：常规长度下一律按显示路径原样打开。
fn open_path_for(display: &str) -> String {
    if display.chars().count() >= 260 {
        request::long_path_for(display)
    } else {
        display.to_string()
    }
}

fn io_error(path: String, e: &std::io::Error) -> CoreError {
    CoreError::new(IO_ERROR)
        .with("path", Value::from(path))
        .with("osError", Value::from(e.raw_os_error().unwrap_or(0)))
}

fn read_all(path: &str, display: &str) -> CoreResult<Vec<u8>> {
    fs::read(path).map_err(|e| direct_error(display, &e))
}

fn parse_with_progress(
    data: &[u8],
    progress: &Progress,
    cancelled: &dyn Fn() -> bool,
    pause: &dyn Fn(),
) -> CoreResult<Payload> {
    let mut indexer = Indexer::new();
    let mut pos = 0usize;
    let mut processed = 0usize;
    let mut first = true;
    while pos < data.len() {
        if cancelled() {
            return Err(CoreError::new(CLOSED));
        }
        let nl = data[pos..].iter().position(|b| *b == b'\n').map(|i| pos + i);
        let line_end = nl.unwrap_or(data.len());
        let mut raw = &data[pos..line_end];
        if raw.last() == Some(&b'\r') {
            raw = &raw[..raw.len() - 1];
        }
        let mut line_off = pos;
        let mut line = raw;
        if first && line.starts_with(&[0xEF, 0xBB, 0xBF]) {
            line_off += 3;
            line = &line[3..];
        }
        first = false;
        indexer.add_line(line, line_off as u64);
        pos = match nl {
            Some(n) => n + 1,
            None => data.len(),
        };
        if pos - processed >= (1 << 20) {
            processed = pos;
            pause();
            progress.advance(pos as u64);
        }
    }
    progress.advance(data.len() as u64);
    Ok(indexer.into_payload())
}

// ── 装载线程 ──

fn run_loader(inner: Arc<Mutex<Inner>>, shared: Arc<Shared>) {
    shared.wait_loader_gate();
    enter_stage(&shared, Stage::Opening, None);
    let route = inner.lock().unwrap().route;
    let result = match route {
        Route::LocalDirect => load_local(&inner, &shared),
        Route::ProxySpool => load_proxy(&inner, &shared),
        Route::DeviceStream => Ok(()),
    };
    match result {
        Ok(()) => {
            shared.state.store(State::Loaded as u8, Ordering::SeqCst);
            shared.events.push(Event::Done);
        }
        Err(e) if e.code == CLOSED => {
            cleanup_resources(&mut inner.lock().unwrap());
            shared.state.store(State::Closed as u8, Ordering::SeqCst);
            shared.events.clear();
        }
        Err(e) => {
            cleanup_resources(&mut inner.lock().unwrap());
            shared.state.store(State::Failed as u8, Ordering::SeqCst);
            shared.events.push(Event::Error(e));
        }
    }
}

fn pause_hook(_shared: &Shared) -> impl Fn() + '_ {
    move || {
        #[cfg(test)]
        if _shared.slow_parse.load(Ordering::SeqCst) {
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
    }
}

fn check_cancel_hook(shared: &Shared) -> impl Fn() -> bool + '_ {
    move || shared.cancelled.load(Ordering::SeqCst)
}

fn load_local(inner: &Arc<Mutex<Inner>>, shared: &Arc<Shared>) -> CoreResult<()> {
    let (display, open_path, plan_identity, cache, cache_hit) = {
        let mut g = inner.lock().unwrap();
        let display = g.request.path.clone().unwrap();
        let open_path = open_path_for(&display);
        (
            display,
            open_path,
            g.plan_identity.clone(),
            g.cache.clone(),
            g.cache_hit.take(),
        )
    };

    let identity = Identity::capture(Path::new(&open_path))
        .map_err(|e| direct_error(&display, &e))?;
    check_cancel(shared)?;

    let changed = plan_identity.as_ref() != Some(&identity);
    if changed {
        let mut g = inner.lock().unwrap();
        if let Some(desc) = g.descriptor.as_mut() {
            desc.identity = Some(identity.clone());
            desc.load_mode = LoadMode::Progressive;
        }
    }
    let hit = if changed { None } else { cache_hit };

    let (payload, doc_size) = match hit {
        Some(payload) => {
            // 缓存不含文本，命中仍须打开源文件取文本
            File::open(&open_path).map_err(|e| direct_error(&display, &e))?;
            (payload, identity.size)
        }
        None => {
            enter_stage(shared, Stage::Indexing, Some(identity.size));
            let data = read_all(&open_path, &display)?;
            check_cancel(shared)?;
            let payload = parse_with_progress(
                &data,
                &shared.progress,
                &check_cancel_hook(shared),
                &pause_hook(shared),
            )?;
            if let (Some(store), Ok(after)) = (&cache, Identity::capture(Path::new(&open_path))) {
                if store.save(&display, Some(&identity), &after, &payload) {
                    let mut g = inner.lock().unwrap();
                    if !g.cache_marked {
                        store.mark_in_use(&display);
                        g.cache_marked = true;
                    }
                }
            }
            (payload, data.len() as u64)
        }
    };

    check_cancel(shared)?;
    store_result(inner, shared, payload, doc_size);
    Ok(())
}

fn load_proxy(inner: &Arc<Mutex<Inner>>, shared: &Arc<Shared>) -> CoreResult<()> {
    let (dest, total) = {
        let g = inner.lock().unwrap();
        if g.spool_dir.is_none() || g.spool_marker.is_none() {
            return Err(CoreError::new(INTERNAL));
        }
        let dest = PathBuf::from(g.descriptor.as_ref().unwrap().source_path.clone());
        let total = g.lease.as_ref().map(|l| l.size).unwrap_or(0);
        (dest, total)
    };

    enter_stage(shared, Stage::Spooling, Some(total));
    let mut lease = inner
        .lock()
        .unwrap()
        .lease
        .take()
        .ok_or_else(|| CoreError::new(INTERNAL))?;
    let result = proxy::spool(
        &mut lease,
        &dest,
        |n| shared.progress.advance(n),
        check_cancel_hook(shared),
    );
    lease.release();
    result?;
    check_cancel(shared)?;

    let (probe_window, probe_threshold) = {
        let g = inner.lock().unwrap();
        (
            g.request.options.probe_window_bytes,
            g.request.options.probe_threshold,
        )
    };
    if let Ok(mut f) = File::open(&dest) {
        if let Ok(sample) = probe::read_window(&mut f, probe_window) {
            if let Some(fmt) = format::detect_unsupported_format(&sample) {
                return Err(reject_unsupported(fmt));
            }
            if !probe::sample_is_text(&sample, probe_threshold) {
                return Err(reject_unsupported("Binary"));
            }
        }
    }

    enter_stage(shared, Stage::Indexing, Some(total));
    let data = fs::read(&dest)
        .map_err(|e| io_error(dest.to_string_lossy().to_string(), &e))?;
    let payload = parse_with_progress(
        &data,
        &shared.progress,
        &check_cancel_hook(shared),
        &pause_hook(shared),
    )?;
    check_cancel(shared)?;
    store_result(inner, shared, payload, data.len() as u64);
    Ok(())
}

fn store_result(inner: &Arc<Mutex<Inner>>, shared: &Shared, payload: Payload, doc_size: u64) {
    let rows = payload.row_count() as u64;
    shared.rows.store(rows, Ordering::SeqCst);
    shared.doc_size.store(doc_size, Ordering::SeqCst);
    let mut g = inner.lock().unwrap();
    if let Some(desc) = g.descriptor.as_mut() {
        desc.row_count = rows;
        desc.doc_size = doc_size;
    }
    g.payload = Some(payload);
}

// ── 消化线程 ──

fn spawn_consumer(
    rx: Receiver<Batch>,
    file: File,
    path: PathBuf,
    shared: Arc<Shared>,
) -> JoinHandle<()> {
    std::thread::spawn(move || consume(rx, file, path, shared))
}

fn consume(rx: Receiver<Batch>, mut file: File, path: PathBuf, shared: Arc<Shared>) {
    let mut indexer = Indexer::new();
    let mut written = 0u64;
    let mut prev_rows = 0u64;
    let mut failed = false;

    while let Ok(batch) = rx.recv() {
        let n = batch.len() as u64;
        if shared.discard.load(Ordering::SeqCst) || failed || batch.is_empty() {
            take_queued(&shared, n);
            continue;
        }
        #[cfg(test)]
        if shared.slow_consume.load(Ordering::SeqCst) {
            std::thread::sleep(std::time::Duration::from_millis(3));
        }

        let mut write_failed = None;
        for line in &batch {
            #[cfg(test)]
            if shared.fail_writes.load(Ordering::SeqCst) {
                write_failed = Some(std::io::Error::from_raw_os_error(5));
                break;
            }
            if let Err(e) = file
                .write_all(line)
                .and_then(|_| file.write_all(b"\r\n"))
            {
                write_failed = Some(e);
                break;
            }
            indexer.add_line(line, written);
            written += (line.len() + 2) as u64;
        }

        if let Some(e) = write_failed {
            failed = true;
            shared.discard.store(true, Ordering::SeqCst);
            shared.queued_rows.store(0, Ordering::SeqCst);
            shared.state.store(State::Failed as u8, Ordering::SeqCst);
            shared.events.push(Event::Error(
                CoreError::new(IO_ERROR)
                    .with("path", Value::from(path.to_string_lossy().to_string()))
                    .with("osError", Value::from(e.raw_os_error().unwrap_or(0))),
            ));
            continue;
        }

        let _ = file.flush();
        let rows = indexer.row_count();
        shared.rows.store(rows, Ordering::SeqCst);
        shared.doc_size.store(written, Ordering::SeqCst);
        {
            // 与 stop 的「入排空快照」互斥：本批要么计入快照，要么计入排空 value。
            let _drain = shared.drain_sync.lock().unwrap();
            let draining = shared.progress.stage() == Stage::Draining;
            take_queued(&shared, n);
            if draining {
                shared.progress.add(n);
            } else {
                shared.progress.advance(rows);
            }
        }
        if rows > prev_rows {
            shared.events.push(Event::Appended {
                rows: rows - prev_rows,
                total_rows: rows,
            });
            prev_rows = rows;
        }
    }
    let _ = file.flush();
    shared.consumer_finished();
}

fn take_queued(shared: &Shared, n: u64) {
    let mut current = shared.queued_rows.load(Ordering::SeqCst);
    loop {
        let next = current.saturating_sub(n);
        match shared.queued_rows.compare_exchange(
            current,
            next,
            Ordering::SeqCst,
            Ordering::SeqCst,
        ) {
            Ok(_) => break,
            Err(actual) => current = actual,
        }
    }
}

// ── 命名（D7）──

static SEQ: AtomicU64 = AtomicU64::new(0);

fn next_seq() -> u64 {
    SEQ.fetch_add(1, Ordering::SeqCst) + 1
}

/// 测试用：把全局序号拨到指定值，便于构造命名冲突。
#[cfg(test)]
fn set_seq_for_test(v: u64) {
    SEQ.store(v, Ordering::SeqCst);
}

fn spool_file_name(display: &str) -> String {
    Path::new(display)
        .file_name()
        .map(|s| s.to_string_lossy().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "spool.log".to_string())
}

fn planned_spool_path(temp: &Path, display: &str) -> PathBuf {
    temp.join(format!("logcat_spool_{}", next_seq()))
        .join(spool_file_name(display))
}

/// 先取得同名锁标记、再独占创建暂存目录（D7）；已存在则递增序号重试。
fn reserve_spool(temp: &Path, display: &str) -> std::io::Result<(PathBuf, File, PathBuf)> {
    fs::create_dir_all(temp)?;
    loop {
        let dir = temp.join(format!("logcat_spool_{}", next_seq()));
        let marker_path = locks::marker_path(&dir);
        let marker = match locks::claim(&dir) {
            Ok(m) => m,
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e),
        };
        match fs::create_dir(&dir) {
            Ok(()) => return Ok((dir.clone(), marker, dir.join(spool_file_name(display)))),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                drop(marker);
                let _ = fs::remove_file(&marker_path);
            }
            Err(e) => {
                drop(marker);
                let _ = fs::remove_file(&marker_path);
                return Err(e);
            }
        }
    }
}

/// 先取得同名锁标记、再独占创建采集文件（D7）；已存在则递增序号重试。
fn create_capture(temp: &Path) -> std::io::Result<(PathBuf, File, File)> {
    fs::create_dir_all(temp)?;
    loop {
        let path = planned_capture_path(temp);
        let marker_path = locks::marker_path(&path);
        let marker = match locks::claim(&path) {
            Ok(m) => m,
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e),
        };
        match File::options().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, marker, file)),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                drop(marker);
                let _ = fs::remove_file(&marker_path);
            }
            Err(e) => {
                drop(marker);
                let _ = fs::remove_file(&marker_path);
                return Err(e);
            }
        }
    }
}

fn planned_capture_path(temp: &Path) -> PathBuf {
    temp.join(format!(
        "logcat_live_{}_{}.log",
        compact_timestamp(),
        next_seq()
    ))
}

fn compact_timestamp() -> String {
    let secs = SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    let (y, m, d) = civil_from_days(days);
    format!(
        "{:04}{:02}{:02}_{:02}{:02}{:02}",
        y,
        m,
        d,
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

/// Howard Hinnant 的 civil_from_days：天数 → (年, 月, 日)。
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

#[cfg(test)]
#[path = "source_tests.rs"]
mod tests;