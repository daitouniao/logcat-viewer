//! 源获取行为测试：状态矩阵、plan/commit、代理 spool、采集推入、收尾与事件。

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use serde_json::json;

use super::{
    create_capture, planned_capture_path, reserve_spool, set_seq_for_test, SourceHandle,
    MAX_FEED_LINES,
};
use crate::error::{
    CLOSED, DIRECT_OPEN_FAILED, FAILED, INVALID_ARG, INVALID_STATE, IO_ERROR, PROXY_DISCONNECT,
    STOPPED, UNSUPPORTED_FORMAT,
};
use crate::events::{Event, Stage};
use crate::locks;
use crate::proxy;
use crate::proxy_support::{test_proxy_exe, TestProxyServer};
use crate::request;
use crate::state::State;
use crate::testutil::{device_request, file_request, temp_dir, text_file, unique_pipe, wait_until};

fn local_plan(dir: &Path, lines: &[&str]) -> (Box<SourceHandle>, PathBuf) {
    let file = text_file(dir, "a.log", lines);
    let req = file_request(
        &file,
        dir,
        &dir.join("cache"),
        &unique_pipe("local"),
        "",
        &[],
        500,
        "Auto",
    );
    (SourceHandle::plan(&req).unwrap(), file)
}

fn device_plan(dir: &Path) -> Box<SourceHandle> {
    SourceHandle::plan(&device_request(dir)).unwrap()
}

fn proxy_plan(dir: &Path, file: &Path) -> (Box<SourceHandle>, String) {
    let pipe = unique_pipe("proxy");
    let args = vec![
        "--serve".to_string(),
        dir.to_string_lossy().to_string(),
    ];
    let req = file_request(
        file,
        dir,
        &dir.join("cache"),
        &pipe,
        &test_proxy_exe(),
        &args,
        5000,
        "On",
    );
    (SourceHandle::plan(&req).unwrap(), pipe)
}

fn wait_state(h: &SourceHandle, state: State) {
    assert!(
        wait_until(|| h.state() == state),
        "状态未到 {:?}，当前 {:?}",
        state,
        h.state()
    );
}

fn wait_rows(h: &SourceHandle, n: u64) {
    assert!(
        wait_until(|| h.shared.rows.load(Ordering::SeqCst) == n),
        "行数未到 {n}，当前 {}",
        h.shared.rows.load(Ordering::SeqCst)
    );
}

/// 后台落盘完成后 `.lcs` 才出现；依赖缓存命中的用例须先等它。
fn wait_lcs(cache: &Path) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let written = fs::read_dir(cache).ok().is_some_and(|rd| {
            rd.flatten()
                .any(|e| e.path().extension().and_then(|s| s.to_str()) == Some("lcs"))
        });
        if written {
            return;
        }
        assert!(Instant::now() < deadline, "sidecar 后台落盘未完成");
        std::thread::sleep(Duration::from_millis(5));
    }
}

fn stages(events: &[Event]) -> Vec<&'static str> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::Stage { stage, .. } => Some(stage.wire()),
            _ => None,
        })
        .collect()
}

fn appended(events: &[Event]) -> Vec<(u64, u64)> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::Appended { rows, total_rows } => Some((*rows, *total_rows)),
            _ => None,
        })
        .collect()
}

fn lines_of(path: &Path) -> Vec<String> {
    fs::read_to_string(path)
        .unwrap()
        .split("\r\n")
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .collect()
}

fn batch(prefix: &str, count: usize) -> Vec<Vec<u8>> {
    (0..count)
        .map(|i| format!("11-14 12:00:01.123  1  2 I Tag: {prefix}{i}").into_bytes())
        .collect()
}

fn exclusive_hold(path: &Path) -> File {
    use std::os::windows::fs::OpenOptionsExt;
    OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(path)
        .unwrap()
}

fn exclusive_blocked(path: &Path) -> bool {
    use std::os::windows::fs::OpenOptionsExt;
    OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(path)
        .is_err()
}

fn stop_in_background(h: &SourceHandle) -> std::thread::JoinHandle<i32> {
    let ptr = h as *const SourceHandle as usize;
    std::thread::spawn(move || {
        let handle = unsafe { &*(ptr as *const SourceHandle) };
        match handle.stop() {
            Ok(()) => 0,
            Err(e) => e.code,
        }
    })
}

// ── 1.5 状态机 ──

#[test]
fn state_cancel_during_commit_closes_handle() {
    let dir = temp_dir("st-cancel-commit");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(h.state(), State::Committing);
    h.cancel().unwrap();
    assert_eq!(h.state(), State::Closed);
    assert_eq!(h.poll_events().unwrap_err().code, CLOSED);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_cancel_after_load_invalid() {
    let dir = temp_dir("st-cancel-loaded");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    assert_eq!(h.cancel().unwrap_err().code, INVALID_STATE);
    assert_eq!(h.state(), State::Loaded);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_poll_before_commit_is_empty() {
    let dir = temp_dir("st-poll-plan");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    assert!(h.poll_events().unwrap().is_empty());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_progress_before_commit_is_initial() {
    let dir = temp_dir("st-progress-plan");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let progress = h.progress_json().unwrap();
    assert_eq!(progress["stage"], "Opening");
    assert_eq!(progress["value"], 0);
    assert!(progress["total"].is_null());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_descriptor_before_commit_invalid() {
    let dir = temp_dir("st-desc-plan");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    assert_eq!(h.descriptor_json().unwrap_err().code, INVALID_STATE);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_descriptor_readable_while_committing() {
    let dir = temp_dir("st-desc-committing");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(h.state(), State::Committing);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["channel"], "Local");
    assert_eq!(desc["rowCount"], 0);
    h.release_loader();
    wait_state(&h, State::Loaded);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_close_aborts_commit() {
    let dir = temp_dir("st-close-commit");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    h.close().unwrap();
    assert_eq!(h.state(), State::Closed);
    assert_eq!(h.progress_json().unwrap_err().code, CLOSED);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_close_from_plan_ready_equals_cancel() {
    let dir = temp_dir("st-close-plan");
    let (a, _fa) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let (b, _fb) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    a.close().unwrap();
    b.cancel().unwrap();
    assert_eq!(a.state(), State::Closed);
    assert_eq!(b.state(), State::Closed);
    assert_eq!(a.poll_events().unwrap_err().code, CLOSED);
    assert_eq!(b.poll_events().unwrap_err().code, CLOSED);
    drop(a);
    drop(b);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_feed_during_stopping_returns_stopped() {
    let dir = temp_dir("st-feed-stopping");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..40 {
        h.feed(batch(&format!("b{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    assert_eq!(h.feed(batch("late", 1)).unwrap_err().code, STOPPED);
    assert_eq!(stop.join().unwrap(), 0);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_stop_during_stopping_idempotent() {
    let dir = temp_dir("st-stop-idem");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..30 {
        h.feed(batch(&format!("s{i}-"), 10)).unwrap();
    }
    let a = stop_in_background(&h);
    let b = stop_in_background(&h);
    assert_eq!(a.join().unwrap(), 0);
    assert_eq!(b.join().unwrap(), 0);
    let stopped = h
        .poll_events()
        .unwrap()
        .iter()
        .filter(|e| e.kind() == "stopped")
        .count();
    assert_eq!(stopped, 1, "重复 stop 只发一次 stopped");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_close_during_stopping_aborts_drain() {
    let dir = temp_dir("st-close-stopping");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..40 {
        h.feed(batch(&format!("c{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    h.close().unwrap();
    assert_eq!(stop.join().unwrap(), CLOSED);
    assert_eq!(h.state(), State::Closed);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn state_commit_precheck_failure_keeps_plan_ready() {
    proxy::shutdown();
    let dir = temp_dir("st-precheck");
    let file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: a"]);
    let (h, pipe) = proxy_plan(&dir, &file);
    let derived = format!("{pipe}-{}", std::process::id());
    assert_eq!(proxy::registry_user_count(&derived), Some(1));
    proxy::shutdown();
    let err = h.commit().unwrap_err();
    assert_eq!(err.code, PROXY_DISCONNECT);
    assert_eq!(
        err.params.get("reason").and_then(|v| v.as_str()),
        Some("planConnectionLost")
    );
    assert_eq!(h.state(), State::PlanReady, "受理前失败不改状态");
    assert_eq!(h.descriptor_json().unwrap_err().code, INVALID_STATE);
    assert_eq!(h.commit().unwrap_err().code, INVALID_STATE);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 2.3 plan 语义 ──

#[test]
fn plan_has_no_side_effects() {
    let dir = temp_dir("plan-no-side");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    assert!(!dir.join("cache").exists(), "plan 不得创建缓存目录");
    let entries: Vec<_> = fs::read_dir(&dir).unwrap().flatten().collect();
    assert_eq!(entries.len(), 1, "plan 不得产生临时产物");
    assert_eq!(entries[0].path(), file);
    assert_eq!(h.state(), State::PlanReady);
    assert!(h.poll_events().unwrap().is_empty());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_estimated_size_sources() {
    let dir = temp_dir("plan-size");
    let (local, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    assert_eq!(
        local.plan_json()["estimatedSize"].as_u64(),
        Some(fs::metadata(&file).unwrap().len())
    );
    local.close().unwrap();

    let proxy_file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: p"]);
    let (spool, _pipe) = proxy_plan(&dir, &proxy_file);
    assert_eq!(
        spool.plan_json()["estimatedSize"].as_u64(),
        Some(fs::metadata(&proxy_file).unwrap().len())
    );
    spool.close().unwrap();

    let device = device_plan(&dir);
    assert!(device.plan_json()["estimatedSize"].is_null());
    device.close().unwrap();

    drop(local);
    drop(spool);
    drop(device);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn path_display_verbatim() {
    let dir = temp_dir("plan-verbatim");
    let file = text_file(&dir, "v.log", &["11-14 12:00:01.123  1  2 I T: v"]);
    let slashed = file.to_string_lossy().replace('\\', "/");
    let req = file_request(
        Path::new(&slashed),
        &dir,
        &dir.join("cache"),
        &unique_pipe("verbatim"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["displayPath"], slashed.as_str());
    assert_eq!(desc["sourcePath"], slashed.as_str());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 2.4 identity ──

#[test]
fn descriptor_available_while_committing() {
    let dir = temp_dir("desc-committing");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let size = fs::metadata(&file).unwrap().len();
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(h.state(), State::Committing);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["rowCount"], 0, "装载未完成时行数为当前值");
    assert_eq!(desc["channel"], "Local");
    assert_eq!(desc["displayPath"], file.to_string_lossy().as_ref());
    assert_eq!(desc["sourcePath"], file.to_string_lossy().as_ref());
    h.release_loader();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["docSize"].as_u64(), Some(size));
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn identity_computed_at_plan() {
    let dir = temp_dir("identity-plan");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let size = fs::metadata(&file).unwrap().len();
    h.hold_loader();
    h.commit().unwrap();
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["identity"]["size"].as_u64(), Some(size));
    assert!(desc["identity"]["mtimeMs"].as_u64().unwrap() > 0);
    assert_eq!(desc["identity"]["headHash"].as_str().unwrap().len(), 16);
    assert_eq!(desc["identity"]["tailHash"].as_str().unwrap().len(), 16);
    h.release_loader();
    wait_state(&h, State::Loaded);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn identity_change_after_plan_degrades() {
    let dir = temp_dir("identity-recheck");
    let file = text_file(&dir, "r.log", &["11-14 12:00:01.123  1  2 I T: first"]);
    let cache = dir.join("cache");
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("recheck"),
        "",
        &[],
        500,
        "Auto",
    );

    let warm = SourceHandle::plan(&req).unwrap();
    warm.commit().unwrap();
    wait_state(&warm, State::Loaded);
    wait_lcs(&cache);
    warm.close().unwrap();
    drop(warm);

    let h = SourceHandle::plan(&req).unwrap();
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(
        h.descriptor_json().unwrap()["loadMode"],
        "OneShot",
        "plan 阶段命中缓存"
    );
    fs::OpenOptions::new()
        .append(true)
        .open(&file)
        .unwrap()
        .write_all(b"11-14 12:00:02.000  1  2 I T: second\n")
        .unwrap();
    let new_size = fs::metadata(&file).unwrap().len();
    h.release_loader();
    wait_state(&h, State::Loaded);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["identity"]["size"].as_u64(), Some(new_size));
    assert_eq!(desc["loadMode"], "Progressive", "复核不一致转完整解析");
    assert_eq!(desc["rowCount"], 2);
    let events = h.poll_events().unwrap();
    assert!(stages(&events).contains(&"Indexing"), "不得沿用旧缓存");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn identity_null_for_proxy_spool() {
    let dir = temp_dir("identity-proxy");
    let file = text_file(&dir, "x.log", &["11-14 12:00:01.123  1  2 I T: x"]);
    let (h, _pipe) = proxy_plan(&dir, &file);
    h.commit().unwrap();
    assert!(h.descriptor_json().unwrap()["identity"].is_null());
    wait_state(&h, State::Loaded);
    assert!(h.descriptor_json().unwrap()["identity"].is_null());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 3.1 代理 ──

#[test]
fn plan_rejects_non_text_when_auto() {
    let dir = temp_dir("plan-reject-auto");
    let file = dir.join("binary.log");
    fs::write(&file, vec![0x01u8; 256]).unwrap();
    let req = file_request(
        &file,
        &dir,
        &dir.join("cache"),
        &unique_pipe("reject-auto"),
        "",
        &[],
        500,
        "Auto",
    );
    let err = match SourceHandle::plan(&req) {
        Ok(_) => panic!("非文本应被拒绝"),
        Err(e) => e,
    };
    assert_eq!(err.code, UNSUPPORTED_FORMAT);
    assert_eq!(
        err.params.get("format").and_then(|v| v.as_str()),
        Some("Binary")
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_rejects_non_text_in_off_mode() {
    let dir = temp_dir("plan-reject-off");
    let file = dir.join("binary.log");
    fs::write(&file, vec![0x01u8; 256]).unwrap();
    let req = file_request(
        &file,
        &dir,
        &dir.join("cache"),
        &unique_pipe("reject-off"),
        "",
        &[],
        500,
        "Off",
    );
    let err = match SourceHandle::plan(&req) {
        Ok(_) => panic!("非文本应被拒绝"),
        Err(e) => e,
    };
    assert_eq!(err.code, UNSUPPORTED_FORMAT);
    assert_eq!(
        err.params.get("format").and_then(|v| v.as_str()),
        Some("Binary")
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_rejects_dlt_magic_with_label() {
    let dir = temp_dir("plan-reject-dlt");
    let file = dir.join("sample.dlt");
    let mut bytes = vec![0x44u8, 0x4C, 0x54, 0x01];
    bytes.extend(vec![0x01u8; 252]);
    fs::write(&file, bytes).unwrap();
    let req = file_request(
        &file,
        &dir,
        &dir.join("cache"),
        &unique_pipe("reject-dlt"),
        "",
        &[],
        500,
        "Auto",
    );
    let err = match SourceHandle::plan(&req) {
        Ok(_) => panic!("DLT 魔数应先于嗅探给出格式标签"),
        Err(e) => e,
    };
    assert_eq!(err.code, UNSUPPORTED_FORMAT);
    assert_eq!(err.params.get("format").and_then(|v| v.as_str()), Some("Dlt"));
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_rejection_creates_no_cache_entry() {
    let dir = temp_dir("plan-reject-cache");
    let cache = dir.join("cache");
    let file = dir.join("binary.log");
    fs::write(&file, vec![0x01u8; 256]).unwrap();
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("reject-cache"),
        "",
        &[],
        500,
        "Auto",
    );
    assert!(SourceHandle::plan(&req).is_err());
    assert!(!cache.exists(), "拒绝不得创建缓存目录");
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_empty_file_stays_local_direct() {
    let dir = temp_dir("plan-empty");
    let file = dir.join("empty.log");
    fs::write(&file, b"").unwrap();
    let req = file_request(
        &file,
        &dir,
        &dir.join("cache"),
        &unique_pipe("empty"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    assert_eq!(h.plan_json()["route"], "LocalDirect");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_auto_falls_back_to_proxy_on_open_error() {
    let dir = temp_dir("plan-fallback-open");
    let serve = temp_dir("plan-fallback-serve");
    let file = text_file(&dir, "held.log", &["11-14 12:00:01.123  1  2 I T: a"]);
    // 代理按 basename 回退到 serve 目录的副本，避免与本机独占占用冲突
    text_file(&serve, "held.log", &["11-14 12:00:01.123  1  2 I T: b"]);
    let hold = exclusive_hold(&file);
    let pipe = unique_pipe("fallback-open");
    let _server = TestProxyServer::start_with_dir(&pipe, &serve);
    let req = file_request(
        &file,
        &dir,
        &dir.join("cache"),
        &pipe,
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    assert_eq!(h.plan_json()["route"], "ProxySpool", "直开失败回退代理");
    h.close().unwrap();
    drop(h);
    drop(hold);
    let _ = fs::remove_dir_all(&dir);
    let _ = fs::remove_dir_all(&serve);
}

// ── 4.1 暂存副本判定 ──

#[test]
fn commit_rejects_non_text_spool() {
    proxy::shutdown();
    let dir = temp_dir("commit-reject-spool");
    let cache = dir.join("cache");
    let file = dir.join("binary.log");
    fs::write(&file, vec![0x01u8; 4096]).unwrap();
    let pipe = unique_pipe("commit-reject");
    let _server = TestProxyServer::start_with_dir(&pipe, &dir);
    let req = file_request(&file, &dir, &cache, &pipe, "", &[], 5000, "On");
    let h = SourceHandle::plan(&req).unwrap();
    assert_eq!(h.plan_json()["route"], "ProxySpool");
    h.commit().unwrap();
    wait_state(&h, State::Failed);
    let events = h.poll_events().unwrap();
    assert_eq!(stages(&events), ["Opening", "Spooling"], "拒绝先于索引阶段");
    let err = events
        .iter()
        .find_map(|e| match e {
            Event::Error(e) => Some(e),
            _ => None,
        })
        .expect("非文本拒绝经错误事件上报");
    assert_eq!(err.code, UNSUPPORTED_FORMAT);
    assert_eq!(
        err.params.get("format").and_then(|v| v.as_str()),
        Some("Binary")
    );
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 0);
    assert!(!cache.exists(), "拒绝不写缓存");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_text_spool_still_loads() {
    let dir = temp_dir("commit-text-spool");
    let file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: p"]);
    let (h, _pipe) = proxy_plan(&dir, &file);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 1);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_cancel_releases_proxy() {
    proxy::shutdown();
    let dir = temp_dir("proxy-cancel");
    let file = text_file(&dir, "c.log", &["11-14 12:00:01.123  1  2 I T: c"]);
    let (h, pipe) = proxy_plan(&dir, &file);
    let derived = format!("{pipe}-{}", std::process::id());
    assert_eq!(proxy::registry_user_count(&derived), Some(1));
    h.cancel().unwrap();
    assert_eq!(h.state(), State::Closed);
    assert_eq!(proxy::registry_user_count(&derived), None);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_close_releases_proxy() {
    proxy::shutdown();
    let dir = temp_dir("proxy-close");
    let file = text_file(&dir, "c.log", &["11-14 12:00:01.123  1  2 I T: c"]);
    let (h, pipe) = proxy_plan(&dir, &file);
    let derived = format!("{pipe}-{}", std::process::id());
    assert_eq!(proxy::registry_user_count(&derived), Some(1));
    h.close().unwrap();
    assert_eq!(proxy::registry_user_count(&derived), None);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_precheck_lost_connection_releases_proxy() {
    proxy::shutdown();
    let dir = temp_dir("proxy-lost");
    let file = text_file(&dir, "l.log", &["11-14 12:00:01.123  1  2 I T: l"]);
    let (h, pipe) = proxy_plan(&dir, &file);
    let derived = format!("{pipe}-{}", std::process::id());
    assert_eq!(proxy::registry_user_count(&derived), Some(1));
    proxy::shutdown();
    assert_eq!(h.commit().unwrap_err().code, PROXY_DISCONNECT);
    assert_eq!(proxy::registry_user_count(&derived), None, "计划连接计数已归零");
    assert_eq!(h.state(), State::PlanReady);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn spool_dir_unique_on_collision() {
    let temp = temp_dir("spool-collision");
    set_seq_for_test(1000);
    let occupied = temp.join("logcat_spool_1001");
    let held = locks::claim(&occupied).unwrap();
    let (dir, marker, dest) = reserve_spool(&temp, "a.log").unwrap();
    assert_ne!(dir, occupied, "命名冲突应递增序号重试");
    assert!(dir.exists());
    assert_eq!(dest.file_name().unwrap(), "a.log");
    drop(marker);
    drop(held);
    fs::remove_dir_all(&dir).unwrap();
    let _ = fs::remove_file(locks::marker_path(&occupied));
    let _ = fs::remove_dir_all(&temp);
}

#[test]
fn spool_lock_marker_held_while_open() {
    proxy::shutdown();
    let dir = temp_dir("spool-marker");
    let file = text_file(&dir, "m.log", &["11-14 12:00:01.123  1  2 I T: m"]);
    let (h, _pipe) = proxy_plan(&dir, &file);
    h.hold_loader();
    h.commit().unwrap();
    let dest = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    let spool_dir = dest.parent().unwrap().to_path_buf();
    let marker = locks::marker_path(&spool_dir);
    assert!(spool_dir.exists());
    assert!(marker.exists());
    assert!(exclusive_blocked(&marker), "打开期间标记必须被占用");
    h.release_loader();
    wait_state(&h, State::Loaded);
    assert!(exclusive_blocked(&marker), "读完后标记仍被持有");
    h.close().unwrap();
    assert!(!marker.exists());
    assert!(!spool_dir.exists());
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn spool_close_best_effort_when_locked() {
    let dir = temp_dir("spool-locked");
    let file = text_file(&dir, "k.log", &["11-14 12:00:01.123  1  2 I T: k"]);
    let (h, _pipe) = proxy_plan(&dir, &file);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let dest = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    let spool_dir = dest.parent().unwrap().to_path_buf();
    let held = exclusive_hold(&dest);
    h.close().unwrap();
    assert!(!locks::marker_path(&spool_dir).exists(), "标记已释放");
    assert!(spool_dir.exists(), "被占用删不掉时残留交维护");
    drop(held);
    let _ = fs::remove_dir_all(&spool_dir);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn proxy_survives_until_last_user_close() {
    proxy::shutdown();
    let dir = temp_dir("proxy-share");
    let file = text_file(&dir, "s.log", &["11-14 12:00:01.123  1  2 I T: s"]);
    let pipe = unique_pipe("share");
    let args = vec![
        "--serve".to_string(),
        dir.to_string_lossy().to_string(),
    ];
    let exe = test_proxy_exe();
    let make = || {
        file_request(
            &file,
            &dir,
            &dir.join("cache"),
            &pipe,
            &exe,
            &args,
            5000,
            "On",
        )
    };
    let a = SourceHandle::plan(&make()).unwrap();
    let b = SourceHandle::plan(&make()).unwrap();
    let derived = format!("{pipe}-{}", std::process::id());
    assert_eq!(proxy::registry_user_count(&derived), Some(2));
    a.close().unwrap();
    assert_eq!(
        proxy::registry_user_count(&derived),
        Some(1),
        "关一个源不结束代理"
    );
    b.close().unwrap();
    assert_eq!(proxy::registry_user_count(&derived), None);
    drop(a);
    drop(b);
    let _ = fs::remove_dir_all(&dir);
}

// ── 3.2 commit 装载 ──

#[test]
fn commit_local_direct_parses_known_content() {
    let dir = temp_dir("commit-local");
    let (h, file) = local_plan(
        &dir,
        &[
            "11-14 12:00:01.123  1  2 I T: one",
            "11-14 12:00:02.123  1  2 I T: two",
            "11-14 12:00:03.123  1  2 I T: three",
        ],
    );
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["rowCount"], 3);
    assert_eq!(
        desc["docSize"].as_u64(),
        Some(fs::metadata(&file).unwrap().len())
    );
    let events = h.poll_events().unwrap();
    assert_eq!(stages(&events), ["Opening", "Indexing"]);
    assert_eq!(events.last().unwrap().kind(), "done");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_cancellation_releases_resources() {
    let dir = temp_dir("commit-cancel");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    h.cancel().unwrap();
    assert_eq!(h.state(), State::Closed);
    let leftovers: Vec<_> = fs::read_dir(&dir)
        .unwrap()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with("logcat_"))
        .collect();
    assert!(leftovers.is_empty(), "取消不得残留临时产物");
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_runtime_failure_emitted_as_event() {
    let dir = temp_dir("commit-runtime-fail");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    fs::remove_file(&file).unwrap();
    h.commit().expect("受理时不打开源文件");
    wait_state(&h, State::Failed);
    let events = h.poll_events().unwrap();
    let err = events
        .iter()
        .find_map(|e| match e {
            Event::Error(e) => Some(e),
            _ => None,
        })
        .expect("运行期失败经错误事件");
    assert_eq!(err.code, DIRECT_OPEN_FAILED);
    assert_eq!(
        err.params.get("path").and_then(|v| v.as_str()),
        Some(file.to_string_lossy().as_ref())
    );
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_failure_keeps_descriptor_snapshot() {
    let dir = temp_dir("commit-desc-fail");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let size = fs::metadata(&file).unwrap().len();
    fs::remove_file(&file).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Failed);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["channel"], "Local");
    assert_eq!(desc["format"], "Logcat");
    assert_eq!(desc["encoding"], "Utf8");
    assert_eq!(desc["displayPath"], file.to_string_lossy().as_ref());
    assert_eq!(desc["identity"]["size"].as_u64(), Some(size));
    assert_eq!(desc["rowCount"], 0);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn commit_direct_open_failure_no_fallback() {
    let dir = temp_dir("commit-no-fallback");
    let (h, file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    fs::remove_file(&file).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Failed);
    let events = h.poll_events().unwrap();
    assert!(
        !stages(&events).contains(&"Spooling"),
        "受理后直开失败不回退代理"
    );
    assert_eq!(h.descriptor_json().unwrap()["channel"], "Local");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn format_encoding_fixed_after_load_start() {
    let dir = temp_dir("format-fixed");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    let before = h.descriptor_json().unwrap();
    assert_eq!(before["format"], "Logcat");
    assert_eq!(before["encoding"], "Utf8");
    h.release_loader();
    wait_state(&h, State::Loaded);
    let after = h.descriptor_json().unwrap();
    assert_eq!(after["format"], before["format"]);
    assert_eq!(after["encoding"], before["encoding"]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn long_path_open_succeeds() {
    let root = temp_dir("long-path");
    let mut deep = root.clone();
    let mut i = 0;
    while deep.to_string_lossy().chars().count() < 280 {
        deep.push(format!("seg{i:02}_{}", "x".repeat(20)));
        fs::create_dir(request::long_path_for(&deep.to_string_lossy())).unwrap();
        i += 1;
    }
    let file = deep.join("long.log");
    let display = file.to_string_lossy().to_string();
    assert!(display.chars().count() >= 260);
    fs::write(
        request::long_path_for(&display),
        b"11-14 12:00:01.123  1  2 I T: long\n",
    )
    .unwrap();

    let req = file_request(
        Path::new(&display),
        &root,
        &root.join("cache"),
        &unique_pipe("long"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["displayPath"], display.as_str());
    assert_eq!(desc["rowCount"], 1);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&root);
}

// ── 3.3 多源 ──

#[test]
fn multiple_sources_coexist() {
    let da = temp_dir("multi-a");
    let db = temp_dir("multi-b");
    let df = temp_dir("multi-file");
    let a = device_plan(&da);
    let b = device_plan(&db);
    let (f, _file) = local_plan(&df, &["11-14 12:00:01.123  1  2 I T: f"]);
    a.commit().unwrap();
    b.commit().unwrap();
    f.commit().unwrap();
    a.feed(batch("a", 2)).unwrap();
    b.feed(batch("b", 3)).unwrap();
    wait_rows(&a, 2);
    wait_rows(&b, 3);
    wait_state(&f, State::Loaded);
    a.stop().unwrap();
    b.stop().unwrap();
    assert_eq!(a.descriptor_json().unwrap()["rowCount"], 2);
    assert_eq!(b.descriptor_json().unwrap()["rowCount"], 3);
    a.close().unwrap();
    b.close().unwrap();
    f.close().unwrap();
    drop(a);
    drop(b);
    drop(f);
    for d in [&da, &db, &df] {
        let _ = fs::remove_dir_all(d);
    }
}

#[test]
fn close_one_source_does_not_affect_others() {
    let dir = temp_dir("close-other");
    let (a, _fa) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    let (b, _fb) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: b"]);
    a.commit().unwrap();
    b.commit().unwrap();
    wait_state(&a, State::Loaded);
    wait_state(&b, State::Loaded);
    a.close().unwrap();
    assert_eq!(b.state(), State::Loaded);
    assert_eq!(b.descriptor_json().unwrap()["rowCount"], 1);
    b.close().unwrap();
    drop(a);
    drop(b);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn core_does_not_auto_close_idle_sources() {
    let dir = temp_dir("idle");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    std::thread::sleep(Duration::from_millis(200));
    assert_eq!(h.state(), State::Loaded, "core 不自行回收闲置源");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_order_fixed_sequence() {
    let dir = temp_dir("close-order");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(batch("o", 10)).unwrap();
    wait_rows(&h, 10);
    h.close().unwrap();
    assert_eq!(h.state(), State::Closed);
    assert!(!path.exists(), "采集文件已删除");
    assert!(!locks::marker_path(&path).exists(), "锁标记已删除");
    assert_eq!(h.events_len(), 0, "未取走事件随关闭作废");
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 3.4 sidecar ──

#[test]
fn sidecar_hit_skips_parse() {
    let dir = temp_dir("sidecar-hit");
    let cache = dir.join("cache");
    let file = text_file(
        &dir,
        "h.log",
        &[
            "11-14 12:00:01.123  1  2 I T: one",
            "11-14 12:00:02.123  1  2 I T: two",
        ],
    );
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("hit"),
        "",
        &[],
        500,
        "Auto",
    );
    let first = SourceHandle::plan(&req).unwrap();
    first.commit().unwrap();
    wait_state(&first, State::Loaded);
    wait_lcs(&cache);
    first.close().unwrap();
    drop(first);

    let second = SourceHandle::plan(&req).unwrap();
    second.hold_loader();
    second.commit().unwrap();
    assert_eq!(
        second.descriptor_json().unwrap()["loadMode"],
        "OneShot",
        "命中转为一次性装载"
    );
    second.release_loader();
    wait_state(&second, State::Loaded);
    let events = second.poll_events().unwrap();
    assert!(!stages(&events).contains(&"Indexing"), "命中跳过解析");
    assert_eq!(second.descriptor_json().unwrap()["rowCount"], 2);
    second.close().unwrap();
    drop(second);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn done_before_sidecar_flush() {
    let dir = temp_dir("done-flush");
    let cache = dir.join("cache");
    let file = text_file(&dir, "d.log", &["11-14 12:00:01.123  1  2 I T: d"]);
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("done-flush"),
        "",
        &[],
        500,
        "Auto",
    );
    super::set_flush_delay_ms(800);
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let entry = cache.join(crate::sidecar::derive_name(&file.to_string_lossy()));
    let absent = !entry.exists();
    let events = h.poll_events().unwrap();
    super::set_flush_delay_ms(0);
    assert_eq!(events.last().unwrap().kind(), "done", "完成事件先发");
    assert!(absent, "进入 Loaded 时落盘尚未开始");

    let deadline = Instant::now() + Duration::from_secs(10);
    while !entry.exists() {
        assert!(Instant::now() < deadline, "后台落盘未完成");
        std::thread::sleep(Duration::from_millis(5));
    }
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn sidecar_write_failure_silent() {
    let dir = temp_dir("flush-fail");
    let cache = dir.join("cache");
    fs::write(&cache, b"occupied").unwrap();
    let file = text_file(&dir, "f.log", &["11-14 12:00:01.123  1  2 I T: f"]);
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("flush-fail"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    std::thread::sleep(Duration::from_millis(200));
    assert_eq!(h.state(), State::Loaded, "写失败不影响装载");
    let events = h.poll_events().unwrap();
    assert_eq!(events.last().unwrap().kind(), "done");
    assert!(
        !events.iter().any(|e| e.kind() == "error"),
        "写失败不得产生错误事件"
    );
    assert!(cache.is_file(), "缓存路径保持原状");
    h.close().unwrap();
    drop(h);
    fs::remove_file(&cache).unwrap();
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn plan_header_hit_skips_decode() {
    let dir = temp_dir("plan-header");
    let cache = dir.join("cache");
    let file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: p"]);
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("plan-header"),
        "",
        &[],
        500,
        "Auto",
    );
    let warm = SourceHandle::plan(&req).unwrap();
    warm.commit().unwrap();
    wait_state(&warm, State::Loaded);
    wait_lcs(&cache);
    warm.close().unwrap();
    drop(warm);

    let entry = cache.join(crate::sidecar::derive_name(&file.to_string_lossy()));
    let mut bytes = fs::read(&entry).unwrap();
    for b in bytes[crate::sidecar::HEADER_SIZE..].iter_mut() {
        *b = 0xff;
    }
    fs::write(&entry, &bytes).unwrap();

    let h = SourceHandle::plan(&req).unwrap();
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(
        h.descriptor_json().unwrap()["loadMode"],
        "OneShot",
        "头部完好即判命中（不读列数据）"
    );
    h.release_loader();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 1, "列数据损坏降级重解析");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn corrupt_payload_degrades_to_full_parse() {
    let dir = temp_dir("corrupt-degrade");
    let cache = dir.join("cache");
    let file = text_file(
        &dir,
        "c.log",
        &[
            "11-14 12:00:01.123  1  2 I T: one",
            "11-14 12:00:02.123  1  2 I T: two",
        ],
    );
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("corrupt-degrade"),
        "",
        &[],
        500,
        "Auto",
    );
    let warm = SourceHandle::plan(&req).unwrap();
    warm.commit().unwrap();
    wait_state(&warm, State::Loaded);
    wait_lcs(&cache);
    warm.close().unwrap();
    drop(warm);

    let entry = cache.join(crate::sidecar::derive_name(&file.to_string_lossy()));
    let mut bytes = fs::read(&entry).unwrap();
    for b in bytes[crate::sidecar::HEADER_SIZE..].iter_mut() {
        *b = 0xff;
    }
    fs::write(&entry, &bytes).unwrap();

    let h = SourceHandle::plan(&req).unwrap();
    h.hold_loader();
    h.commit().unwrap();
    assert_eq!(h.descriptor_json().unwrap()["loadMode"], "OneShot");
    h.release_loader();
    wait_state(&h, State::Loaded);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["loadMode"], "Progressive", "解码失败更新为分块渐进（一次）");
    assert_eq!(desc["rowCount"], 2, "结果完整");
    let events = h.poll_events().unwrap();
    assert!(stages(&events).contains(&"Indexing"), "降级走完整解析");
    assert!(
        !events.iter().any(|e| e.kind() == "error"),
        "降级不得产生错误事件"
    );
    assert_eq!(events.last().unwrap().kind(), "done");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn long_format_sample_columns() {
    let dir = temp_dir("long-sample");
    let cache = dir.join("cache");
    let sample = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("testdata")
        .join("long_sample.txt");
    let data = fs::read(&sample).unwrap();
    let file = dir.join("long.log");
    fs::write(&file, &data).unwrap();

    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("long"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 3, "行数不变");
    h.close().unwrap();
    drop(h);

    let deadline = Instant::now() + Duration::from_secs(10);
    let entry = loop {
        let found = fs::read_dir(&cache).ok().and_then(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .find(|p| p.extension().and_then(|s| s.to_str()) == Some("lcs"))
        });
        if let Some(path) = found {
            break path;
        }
        assert!(Instant::now() < deadline, "缓存条目未落盘");
        std::thread::sleep(Duration::from_millis(5));
    };

    let payload = crate::sidecar::decode(&fs::read(&entry).unwrap(), None).unwrap();
    assert_eq!(payload.row_count(), 3);
    assert_eq!(payload.minutes, vec![0, 0, 0]);
    assert_eq!(payload.pids, vec![101, 303, 505]);
    assert_eq!(payload.tids, vec![202, 404, -1]);
    assert_eq!(payload.lvls, vec![3, 4, 5]);
    assert_eq!(payload.tags, vec!["Alpha", "Beta", "Gamma"]);
    assert_eq!(payload.tag_ids, vec![0, 1, 2]);
    let texts: Vec<&str> = (0..3)
        .map(|i| {
            let start = payload.offs[i] as usize + payload.moff[i] as usize;
            let end = payload.offs[i] as usize + payload.lens[i] as usize;
            std::str::from_utf8(&data[start..end]).unwrap()
        })
        .collect();
    assert_eq!(texts, vec!["first line", "second, line", "no-space"]);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn crash_sample_rows_and_dictionaries() {
    let dir = temp_dir("crash-sample");
    let cache = dir.join("cache");
    let sample = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("testdata")
        .join("crash_sample.txt");
    let data = fs::read(&sample).unwrap();
    let file = dir.join("crash.log");
    fs::write(&file, &data).unwrap();

    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("crash"),
        "",
        &[],
        500,
        "Auto",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 9, "空行不进索引");
    h.close().unwrap();
    drop(h);

    let deadline = Instant::now() + Duration::from_secs(10);
    let entry = loop {
        let found = fs::read_dir(&cache).ok().and_then(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .find(|p| p.extension().and_then(|s| s.to_str()) == Some("lcs"))
        });
        if let Some(path) = found {
            break path;
        }
        assert!(Instant::now() < deadline, "缓存条目未落盘");
        std::thread::sleep(Duration::from_millis(5));
    };

    let payload = crate::sidecar::decode(&fs::read(&entry).unwrap(), None).unwrap();
    assert_eq!(payload.row_count(), 9);

    // 崩溃事件按行序：Java / Native / ANR / Tombstone / 独立 signal 行
    let rows: Vec<i32> = payload.crashes.iter().map(|c| c.row).collect();
    assert_eq!(rows, vec![1, 4, 5, 6, 8]);
    let kinds: Vec<u8> = payload.crashes.iter().map(|c| c.kind).collect();
    assert_eq!(kinds, vec![0, 1, 2, 3, 1]);
    let meta: Vec<(i32, i32, i32, i32)> = payload
        .crashes
        .iter()
        .map(|c| (c.exc_id, c.pkg_id, c.pid, c.tid))
        .collect();
    assert_eq!(
        meta,
        vec![
            (0, 0, 100, 200),
            (1, -1, 300, 400),
            (-1, 1, 500, 600),
            (2, -1, -1, -1),
            (2, -1, -1, -1),
        ]
    );
    assert_eq!(
        payload.exc_types,
        vec!["java.lang.RuntimeException", "SIGSEGV", "SIGABRT"]
    );
    assert_eq!(payload.pkgs, vec!["com.example.app", "com.example.anr"]);

    let _ = fs::remove_dir_all(&dir);
}

fn load_sample_payload(
    file: &Path,
    dir: &Path,
    cache: &Path,
    tag: &str,
    chunks: Option<(usize, usize)>,
) -> (crate::sidecar::Payload, usize) {
    let req = file_request(file, dir, cache, &unique_pipe(tag), "", &[], 500, "Auto");
    let h = SourceHandle::plan(&req).unwrap();
    if let Some((first, steady)) = chunks {
        h.set_chunk_sizes(first, steady);
    }
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let segment_count = h.segment_count();
    h.close().unwrap();
    drop(h);

    let deadline = Instant::now() + Duration::from_secs(10);
    let entry = loop {
        let found = fs::read_dir(cache).ok().and_then(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .find(|p| p.extension().and_then(|s| s.to_str()) == Some("lcs"))
        });
        if let Some(path) = found {
            break path;
        }
        assert!(Instant::now() < deadline, "缓存条目未落盘");
        std::thread::sleep(Duration::from_millis(5));
    };
    let payload = crate::sidecar::decode(&fs::read(&entry).unwrap(), None).unwrap();
    (payload, segment_count)
}

#[test]
fn chunked_equals_whole() {
    let dir = temp_dir("chunked-eq");
    let sample = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("testdata")
        .join("crash_sample.txt");
    let file = dir.join("c.log");
    fs::write(&file, fs::read(&sample).unwrap()).unwrap();

    let whole = load_sample_payload(
        &file,
        &dir,
        &dir.join("cache-whole"),
        "chunk-whole",
        None,
    );
    let chunked = load_sample_payload(
        &file,
        &dir,
        &dir.join("cache-chunked"),
        "chunk-tiny",
        Some((16, 16)),
    );

    assert!(chunked.1 > 1, "小分块应产生多段（实际 {}）", chunked.1);
    assert_eq!(chunked.0, whole.0, "8 列 / 字典顺序 / 崩溃行号全等");

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn generated_sample_chunk_boundaries() {
    let dir = temp_dir("sample-gen");
    let gen = crate::sample_gen::write_all(&dir).unwrap();

    let big = fs::read(&gen.big).unwrap();
    assert!(
        big.len() >= crate::sample_gen::BIG_BYTES,
        "大样本不足 40 MiB（{}）",
        big.len()
    );
    let whole = crate::parse::parse_bytes(&big);
    let (chunked, segments) = crate::segment::parse_chunked(&big, 4096, 4096);
    assert_eq!(chunked, whole, "40 MiB 样本：分块 ≡ 整份");
    assert_eq!(
        chunked.crashes.len(),
        gen.big_expected_crashes,
        "崩溃事件数与生成器预期不符"
    );
    let starts: Vec<usize> = segments.iter().skip(1).map(|s| s.start_row).collect();
    let crossing = chunked.crashes.iter().any(|c| {
        let r = c.row.max(0) as usize;
        starts.iter().any(|&b| b > r && b <= r + 3)
    });
    assert!(crossing, "样本未覆盖块边界崩溃");
    assert!(segments.len() > 1, "大样本应切成多段");

    for (path, tag) in [
        (&gen.threadtime, "threadtime"),
        (&gen.long, "long"),
        (&gen.brief, "brief"),
    ] {
        let data = fs::read(path).unwrap();
        assert!(
            data.len() >= crate::sample_gen::FORMAT_BYTES,
            "{tag} 样本尺寸不足"
        );
        let (chunked, segments) = crate::segment::parse_chunked(&data, 8192, 8192);
        assert!(chunked.row_count() > 0, "{tag} 无有效行");
        assert!(segments.len() > 1, "{tag} 应切成多段");
    }

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn sidecar_only_local_direct() {
    let dir = temp_dir("sidecar-local-only");
    let cache = dir.join("cache");
    let file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: p"]);
    let pipe = unique_pipe("local-only");
    let args = vec![
        "--serve".to_string(),
        dir.to_string_lossy().to_string(),
    ];
    let req = file_request(
        &file,
        &dir,
        &cache,
        &pipe,
        &test_proxy_exe(),
        &args,
        5000,
        "On",
    );
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    h.close().unwrap();
    assert!(!cache.exists(), "代理源不得写 sidecar 缓存");
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 4.1 采集 commit ──

#[test]
fn device_commit_skips_probe_and_proxy() {
    let dir = temp_dir("device-commit");
    let h = device_plan(&dir);
    assert_eq!(h.plan_json()["route"], "DeviceStream");
    assert!(h.plan_json()["estimatedSize"].is_null());
    h.commit().unwrap();
    assert_eq!(h.state(), State::Running);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["channel"], "AdbCapture");
    assert!(desc["displayPath"].is_null());
    assert_eq!(desc["appendMode"], "Appendable");
    assert_eq!(desc["leaseKind"], "Ephemeral");
    let spools: Vec<_> = fs::read_dir(&dir)
        .unwrap()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with("logcat_spool_"))
        .collect();
    assert!(spools.is_empty(), "采集不建立代理暂存目录");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn device_capture_file_created_by_core() {
    let dir = temp_dir("device-capture");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let desc = h.descriptor_json().unwrap();
    let path = PathBuf::from(desc["sourcePath"].as_str().unwrap());
    assert!(path.exists(), "core 创建采集文件");
    assert_eq!(fs::metadata(&path).unwrap().len(), 0);
    assert!(desc["leaseId"].as_str().unwrap().starts_with("logcat_live_"));
    assert!(desc["identity"].is_null());

    h.feed(vec![b"aa".to_vec(), b"bb".to_vec()]).unwrap();
    h.stop().unwrap();
    let bytes = fs::read(&path).unwrap();
    assert_eq!(bytes, b"aa\r\nbb\r\n");
    assert!(!bytes.starts_with(&[0xEF, 0xBB, 0xBF]), "UTF-8 无 BOM");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn capture_file_unique_on_collision() {
    let temp = temp_dir("capture-collision");
    set_seq_for_test(2000);
    let occupied = planned_capture_path(&temp);
    let held = locks::claim(&occupied).unwrap();
    File::create(&occupied).unwrap();
    set_seq_for_test(2000);
    let (path, marker, file) = create_capture(&temp).unwrap();
    drop(file);
    assert_ne!(path, occupied);
    assert!(path.exists());
    drop(marker);
    drop(held);
    let _ = fs::remove_file(&path);
    let _ = fs::remove_file(locks::marker_path(&path));
    let _ = fs::remove_file(&occupied);
    let _ = fs::remove_file(locks::marker_path(&occupied));
    let _ = fs::remove_dir_all(&temp);
}

#[test]
fn capture_lock_marker_held_while_open() {
    let dir = temp_dir("capture-marker");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    let marker = locks::marker_path(&path);
    assert!(marker.exists());
    assert!(exclusive_blocked(&marker));
    h.feed(batch("m", 2)).unwrap();
    h.stop().unwrap();
    assert!(path.exists(), "stop 保留采集文件");
    assert!(marker.exists());
    assert!(exclusive_blocked(&marker), "stop 后标记继续被占用");
    h.close().unwrap();
    assert!(!path.exists());
    assert!(!marker.exists());
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn capture_commit_failure_keeps_descriptor_snapshot() {
    let dir = temp_dir("capture-fail");
    let temp_as_file = dir.join("temp-is-file");
    fs::write(&temp_as_file, b"x").unwrap();
    let req = json!({
        "kind": "Device",
        "options": { "tempDir": temp_as_file.to_string_lossy() }
    })
    .to_string();
    let h = SourceHandle::plan(&req).unwrap();
    h.commit().expect("受理失败经事件");
    assert_eq!(h.state(), State::Failed);
    let desc = h.descriptor_json().unwrap();
    assert_eq!(desc["channel"], "AdbCapture");
    assert!(desc["displayPath"].is_null());
    assert!(desc["sourcePath"].as_str().unwrap().contains("logcat_live_"));
    assert_eq!(desc["rowCount"], 0);
    let events = h.poll_events().unwrap();
    let err = events
        .iter()
        .find_map(|e| match e {
            Event::Error(e) => Some(e),
            _ => None,
        })
        .expect("错误事件");
    assert_eq!(err.code, IO_ERROR);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 4.2 feed ──

#[test]
fn feed_enqueues_without_blocking() {
    let dir = temp_dir("feed-nonblock");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    let started = Instant::now();
    for i in 0..50 {
        h.feed(batch(&format!("f{i}-"), 20)).unwrap();
    }
    assert!(
        started.elapsed() < Duration::from_secs(1),
        "入队不得等消化"
    );
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn feed_empty_batch_legal() {
    let dir = temp_dir("feed-empty");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(Vec::new()).unwrap();
    std::thread::sleep(Duration::from_millis(30));
    assert_eq!(h.shared.rows.load(Ordering::SeqCst), 0);
    #[cfg(test)]
    {
        // 受理期间不产生事件；空批也不产生。
        assert_eq!(h.events_len(), 0);
    }
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn feed_over_limit_rejected_with_field() {
    let dir = temp_dir("feed-limit");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let too_many = vec![Vec::new(); MAX_FEED_LINES + 1];
    let err = h.feed(too_many).unwrap_err();
    assert_eq!(err.code, INVALID_ARG);
    assert_eq!(
        err.params.get("field").and_then(|v| v.as_str()),
        Some("lines")
    );
    h.close().unwrap();
    // 参数形状错误与句柄状态无关（D15）：已关闭仍报 INVALID_ARG。
    let again = vec![Vec::new(); MAX_FEED_LINES + 1];
    assert_eq!(h.feed(again).unwrap_err().code, INVALID_ARG);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn feed_over_limit_enqueues_nothing() {
    let dir = temp_dir("feed-limit-none");
    let h = device_plan(&dir);
    h.commit().unwrap();
    assert_eq!(
        h.feed(vec![Vec::new(); MAX_FEED_LINES + 1]).unwrap_err().code,
        INVALID_ARG
    );
    h.feed(batch("ok", 2)).unwrap();
    wait_rows(&h, 2);
    h.stop().unwrap();
    let events = h.poll_events().unwrap();
    assert_eq!(appended(&events), vec![(2, 2)]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 4.3 消化线程 ──

#[test]
fn consume_thread_processes_in_order() {
    let dir = temp_dir("consume-order");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(vec![b"a".to_vec()]).unwrap();
    h.feed(vec![b"b".to_vec(), b"c".to_vec()]).unwrap();
    h.feed(vec![b"d".to_vec()]).unwrap();
    h.stop().unwrap();
    assert_eq!(lines_of(&path), ["a", "b", "c", "d"]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn appended_matches_committed_batches() {
    let dir = temp_dir("appended-batches");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("x", 3)).unwrap();
    wait_rows(&h, 3);
    h.feed(batch("y", 2)).unwrap();
    wait_rows(&h, 5);
    h.stop().unwrap();
    let events = h.poll_events().unwrap();
    assert_eq!(appended(&events), vec![(3, 3), (2, 5)]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn appended_not_emitted_on_zero_growth() {
    let dir = temp_dir("appended-zero");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(vec![
        Vec::new(),
        b"---------- beginning of main".to_vec(),
    ])
    .unwrap();
    assert!(wait_until(|| h.shared.queued_rows.load(Ordering::SeqCst) == 0));
    h.stop().unwrap();
    let events = h.poll_events().unwrap();
    assert!(appended(&events).is_empty(), "零增长不发 appended");
    assert_eq!(h.descriptor_json().unwrap()["rowCount"], 0);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 4.4 stop / close ──

#[test]
fn stop_drains_then_emits_stopped() {
    let dir = temp_dir("stop-drain");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(batch("z", 100)).unwrap();
    h.stop().unwrap();
    assert_eq!(h.state(), State::Stopped);
    assert_eq!(lines_of(&path).len(), 100);
    let events = h.poll_events().unwrap();
    assert_eq!(stages(&events).last(), Some(&"Draining"));
    assert_eq!(events.last().unwrap().kind(), "stopped");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stop_drain_progress_pollable() {
    let dir = temp_dir("stop-progress");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..40 {
        h.feed(batch(&format!("d{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    let mut total = None;
    while let Ok(value) = h.progress_json() {
        if value["stage"] == "Draining" {
            total = value["total"].as_u64();
            assert!(value["value"].as_u64().unwrap() <= total.unwrap_or(u64::MAX));
        }
        if h.state() == State::Stopped {
            break;
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    assert_eq!(stop.join().unwrap(), 0);
    let progress = h.progress_json().unwrap();
    let total = total.expect("排空期间可读进度");
    assert_eq!(progress["stage"], "Draining");
    assert_eq!(progress["total"].as_u64(), Some(total));
    assert_eq!(progress["value"].as_u64(), Some(total), "结束时 value == total");
    assert!(total > 0, "快照应包含未消化队列");
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stop_keeps_capture_file_and_marker() {
    let dir = temp_dir("stop-keep");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(batch("k", 2)).unwrap();
    h.stop().unwrap();
    assert!(path.exists());
    let marker = locks::marker_path(&path);
    assert!(marker.exists());
    assert!(exclusive_blocked(&marker));
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stop_during_stopping_is_idempotent() {
    let dir = temp_dir("stop-idem2");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..20 {
        h.feed(batch(&format!("i{i}-"), 10)).unwrap();
    }
    let a = stop_in_background(&h);
    let b = stop_in_background(&h);
    assert_eq!(a.join().unwrap(), 0);
    assert_eq!(b.join().unwrap(), 0);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn feed_during_stopping_returns_stopped() {
    let dir = temp_dir("feed-stopping");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..30 {
        h.feed(batch(&format!("g{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    assert_eq!(h.feed(batch("late", 1)).unwrap_err().code, STOPPED);
    assert_eq!(stop.join().unwrap(), 0);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn feed_after_stop_returns_stopped() {
    let dir = temp_dir("feed-after-stop");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("a", 2)).unwrap();
    h.stop().unwrap();
    assert_eq!(h.feed(batch("b", 1)).unwrap_err().code, STOPPED);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_during_stopping_aborts_drain() {
    let dir = temp_dir("close-abort");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..30 {
        h.feed(batch(&format!("a{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    h.close().unwrap();
    assert_eq!(stop.join().unwrap(), CLOSED, "被阻塞的 stop 返回 CLOSED");
    assert_eq!(h.state(), State::Closed);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_during_stopping_emits_no_stopped() {
    let dir = temp_dir("close-no-stopped");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..30 {
        h.feed(batch(&format!("n{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    h.close().unwrap();
    assert_eq!(stop.join().unwrap(), CLOSED);
    assert_eq!(h.events_len(), 0, "中止排空不发 stopped");
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_discards_queue_without_draining() {
    let dir = temp_dir("close-discard");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.set_slow_consume(true);
    for i in 0..300 {
        h.feed(batch(&format!("q{i}-"), 10)).unwrap();
    }
    let started = Instant::now();
    h.close().unwrap();
    assert!(
        started.elapsed() < Duration::from_secs(1),
        "close 不等积压消化"
    );
    assert!(!path.exists());
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_does_not_enter_draining() {
    let dir = temp_dir("close-no-draining");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("c", 5)).unwrap();
    let stage_before = h.shared.progress.stage();
    h.close().unwrap();
    assert_eq!(stage_before, Stage::Capturing);
    assert_ne!(
        h.shared.progress.stage(),
        Stage::Draining,
        "close 不进入排空"
    );
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_deletes_capture_file_and_marker() {
    let dir = temp_dir("close-delete");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(batch("x", 2)).unwrap();
    h.close().unwrap();
    assert!(!path.exists());
    assert!(!locks::marker_path(&path).exists());
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_best_effort_when_capture_file_held() {
    let dir = temp_dir("close-held");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(batch("h", 2)).unwrap();
    h.stop().unwrap();
    let held = exclusive_hold(&path);
    h.close().expect("外部占用时 close 仍成功");
    assert!(!locks::marker_path(&path).exists(), "标记已释放");
    assert!(path.exists(), "残留交启动维护");
    drop(held);
    let _ = fs::remove_file(&path);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_emits_no_terminal_event() {
    let dir = temp_dir("close-no-terminal");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("t", 3)).unwrap();
    wait_rows(&h, 3);
    h.close().unwrap();
    assert_eq!(h.events_len(), 0);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn close_discards_unpolled_events() {
    let dir = temp_dir("close-unpolled");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("u", 3)).unwrap();
    wait_rows(&h, 3);
    assert!(h.events_len() > 0, "已产生未取走事件");
    h.close().unwrap();
    assert_eq!(h.events_len(), 0, "关闭作废未取走事件");
    assert_eq!(h.poll_events().unwrap_err().code, CLOSED);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stop_and_close_are_idempotent() {
    let dir = temp_dir("stop-close-idem");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("v", 2)).unwrap();
    h.stop().unwrap();
    h.stop().unwrap();
    h.close().unwrap();
    h.close().unwrap();
    assert_eq!(h.state(), State::Closed);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stop_on_file_source_invalid_state() {
    let dir = temp_dir("stop-file");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    assert_eq!(h.stop().unwrap_err().code, INVALID_STATE);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    assert_eq!(h.stop().unwrap_err().code, INVALID_STATE);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn disk_failure_clears_queue_and_keeps_committed_rows() {
    let dir = temp_dir("disk-fail");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(vec![b"keep-1".to_vec(), b"keep-2".to_vec()]).unwrap();
    wait_rows(&h, 2);
    h.set_fail_writes(true);
    h.feed(batch("fail", 5)).unwrap();
    assert!(wait_until(|| h.state() == State::Failed));
    assert_eq!(h.shared.queued_rows.load(Ordering::SeqCst), 0);
    let events = h.poll_events().unwrap();
    assert!(events
        .iter()
        .any(|e| e.kind() == "error"));
    assert_eq!(lines_of(&path), ["keep-1", "keep-2"]);
    assert_eq!(h.feed(batch("later", 1)).unwrap_err().code, FAILED);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn disk_failure_during_drain_is_terminal_error() {
    let dir = temp_dir("disk-fail-drain");
    let h = device_plan(&dir);
    h.commit().unwrap();
    let path = PathBuf::from(h.descriptor_json().unwrap()["sourcePath"].as_str().unwrap());
    h.feed(vec![b"committed".to_vec()]).unwrap();
    wait_rows(&h, 1);
    h.set_fail_writes(true);
    h.feed(batch("bad", 3)).unwrap();
    let stop = h.stop();
    assert_eq!(stop.unwrap_err().code, FAILED);
    assert_eq!(h.state(), State::Failed);
    let events = h.poll_events().unwrap();
    assert!(events.iter().any(|e| e.kind() == "error"));
    assert!(!events.iter().any(|e| e.kind() == "stopped"), "失败不发 stopped");
    assert_eq!(lines_of(&path), ["committed"]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

// ── 5.1 事件与进度 ──

#[test]
fn appended_carries_delta_and_total() {
    let dir = temp_dir("events-appended");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.feed(batch("a", 3)).unwrap();
    wait_rows(&h, 3);
    h.feed(batch("b", 2)).unwrap();
    wait_rows(&h, 5);
    h.stop().unwrap();
    let events = h.poll_events().unwrap();
    let appended: Vec<_> = appended(&events);
    assert_eq!(appended, vec![(3, 3), (2, 5)]);
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_no_appended_for_file_load() {
    let dir = temp_dir("events-file");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let events = h.poll_events().unwrap();
    assert!(!events.iter().any(|e| e.kind() == "appended"));
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stage_transitions_follow_path() {
    let dir = temp_dir("events-stages");
    let (file_src, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    file_src.commit().unwrap();
    wait_state(&file_src, State::Loaded);
    assert_eq!(
        stages(&file_src.poll_events().unwrap()),
        ["Opening", "Indexing"]
    );
    file_src.close().unwrap();

    let proxy_file = text_file(&dir, "p.log", &["11-14 12:00:01.123  1  2 I T: p"]);
    let (proxy_src, _pipe) = proxy_plan(&dir, &proxy_file);
    proxy_src.commit().unwrap();
    wait_state(&proxy_src, State::Loaded);
    assert_eq!(
        stages(&proxy_src.poll_events().unwrap()),
        ["Opening", "Spooling", "Indexing"]
    );
    proxy_src.close().unwrap();

    let device = device_plan(&dir);
    device.commit().unwrap();
    assert!(device.poll_events().unwrap().is_empty(), "受理不发事件");
    device.feed(batch("s", 1)).unwrap();
    device.stop().unwrap();
    assert_eq!(stages(&device.poll_events().unwrap()), ["Draining"]);
    device.close().unwrap();

    drop(file_src);
    drop(proxy_src);
    drop(device);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn stage_hit_path_skips_indexing() {
    let dir = temp_dir("events-hit-path");
    let cache = dir.join("cache");
    let file = text_file(&dir, "h.log", &["11-14 12:00:01.123  1  2 I T: a"]);
    let req = file_request(
        &file,
        &dir,
        &cache,
        &unique_pipe("hitpath"),
        "",
        &[],
        500,
        "Auto",
    );
    let first = SourceHandle::plan(&req).unwrap();
    first.commit().unwrap();
    wait_state(&first, State::Loaded);
    wait_lcs(&cache);
    first.close().unwrap();
    drop(first);
    let second = SourceHandle::plan(&req).unwrap();
    second.commit().unwrap();
    wait_state(&second, State::Loaded);
    assert_eq!(stages(&second.poll_events().unwrap()), ["Opening"]);
    second.close().unwrap();
    drop(second);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_no_channel_change_after_accept() {
    let dir = temp_dir("events-channel");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.hold_loader();
    h.commit().unwrap();
    let channel = h.descriptor_json().unwrap()["channel"].clone();
    h.release_loader();
    wait_state(&h, State::Loaded);
    assert_eq!(h.descriptor_json().unwrap()["channel"], channel);
    for event in h.poll_events().unwrap() {
        assert!(
            event.to_json().get("channel").is_none(),
            "事件线格式不含渠道变化字段"
        );
    }
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_terminal_per_source_kind() {
    let dir = temp_dir("events-terminal");
    let (file_src, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    file_src.commit().unwrap();
    wait_state(&file_src, State::Loaded);
    let file_events = file_src.poll_events().unwrap();
    let file_terminals: Vec<_> = file_events
        .iter()
        .filter(|e| e.is_terminal())
        .map(|e| e.kind())
        .collect();
    assert_eq!(file_terminals, ["done"]);
    file_src.close().unwrap();

    let device = device_plan(&dir);
    device.commit().unwrap();
    device.feed(batch("t", 2)).unwrap();
    device.stop().unwrap();
    let device_events = device.poll_events().unwrap();
    let device_terminals: Vec<_> = device_events
        .iter()
        .filter(|e| e.is_terminal())
        .map(|e| e.kind())
        .collect();
    assert_eq!(device_terminals, ["stopped"]);
    device.close().unwrap();

    drop(file_src);
    drop(device);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_survive_delayed_poll() {
    let dir = temp_dir("events-delay");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    std::thread::sleep(Duration::from_millis(50));
    let events = h.poll_events().unwrap();
    assert!(events.iter().any(|e| e.kind() == "done"), "迟轮询不丢事件");
    assert!(h.poll_events().unwrap().is_empty());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_no_event_on_commit_accept() {
    let dir = temp_dir("events-accept");
    let (file_src, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    file_src.hold_loader();
    file_src.commit().unwrap();
    assert_eq!(file_src.events_len(), 0, "文件源受理不发事件");
    file_src.release_loader();
    wait_state(&file_src, State::Loaded);
    file_src.close().unwrap();

    let device = device_plan(&dir);
    device.commit().unwrap();
    assert_eq!(device.events_len(), 0, "采集源受理不发事件");
    device.close().unwrap();

    drop(file_src);
    drop(device);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_done_marks_completion() {
    let dir = temp_dir("events-done");
    let (h, _file) = local_plan(&dir, &["11-14 12:00:01.123  1  2 I T: a"]);
    h.commit().unwrap();
    wait_state(&h, State::Loaded);
    let events = h.poll_events().unwrap();
    assert_eq!(events.last().unwrap().kind(), "done");
    assert!(h.poll_events().unwrap().is_empty());
    h.close().unwrap();
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_not_published_for_plan_failure() {
    let dir = temp_dir("events-plan-fail");
    let missing = dir.join("missing.log");
    let req = file_request(
        &missing,
        &dir,
        &dir.join("cache"),
        &unique_pipe("fail"),
        "",
        &[],
        500,
        "Auto",
    );
    let err = SourceHandle::plan(&req).err().unwrap();
    assert_eq!(err.code, crate::error::NOT_FOUND);
    assert_eq!(
        err.params.get("path").and_then(|v| v.as_str()),
        Some(missing.to_string_lossy().as_ref())
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn events_no_stopped_when_close_aborts_drain() {
    let dir = temp_dir("events-no-stopped");
    let h = device_plan(&dir);
    h.commit().unwrap();
    h.set_slow_consume(true);
    for i in 0..30 {
        h.feed(batch(&format!("x{i}-"), 10)).unwrap();
    }
    let stop = stop_in_background(&h);
    assert!(wait_until(|| h.shared.progress.stage() == Stage::Draining));
    h.close().unwrap();
    assert_eq!(stop.join().unwrap(), CLOSED);
    assert_eq!(h.events_len(), 0);
    drop(h);
    let _ = fs::remove_dir_all(&dir);
}