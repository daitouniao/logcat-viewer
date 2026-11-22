//! C ABI 面（design D2 / D8）：JSON 字符串封送、数值错误码、不透明句柄。
//!
//! 所有导出体包 `catch_unwind`，panic 转 `INTERNAL`；跨 FFI 的内存「谁分配谁释放」——
//! core 返回的字符串以 `logcore_free_string` 释放，句柄以 `logcore_free_handle` 释放
//! （且仅在已关闭时合法）。同步失败的错误码与 `params` 经 `logcore_take_last_error`
//! 取走（线程本地，取后清空）。

use std::cell::RefCell;
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};

use serde_json::Value;

use logcore_core::error::{CoreError, CoreResult, INTERNAL, INVALID_STATE, OK};
use logcore_core::events::events_json;
use logcore_core::maintain;
use logcore_core::proxy;
use logcore_core::source::SourceHandle;
use logcore_core::state::{self, Op, State};

thread_local! {
    static LAST_ERROR: RefCell<Option<CoreError>> = const { RefCell::new(None) };
}

fn record(err: CoreError) -> i32 {
    let code = err.code;
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some(err));
    code
}

fn finish(result: CoreResult<()>) -> i32 {
    match result {
        Ok(()) => OK,
        Err(e) => record(e),
    }
}

fn guard<F: FnOnce() -> i32>(f: F) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|_| record(CoreError::new(INTERNAL)))
}

unsafe fn request_text<'a>(ptr: *const c_char) -> CoreResult<&'a str> {
    if ptr.is_null() {
        return Err(CoreError::invalid_arg("request"));
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map_err(|_| CoreError::invalid_arg("request"))
}

fn into_c_string(value: &Value) -> *mut c_char {
    CString::new(value.to_string())
        .expect("JSON 不含 NUL")
        .into_raw()
}

fn write_json_out(out: *mut *mut c_char, value: &Value) -> CoreResult<()> {
    if out.is_null() {
        return Err(CoreError::invalid_arg("out"));
    }
    unsafe { *out = into_c_string(value) };
    Ok(())
}

/// 一行输入：`data` 指向 UTF-8 字节（不含行尾符），`len` 为字节数。
#[repr(C)]
pub struct LogcoreLine {
    pub data: *const u8,
    pub len: usize,
}

/// plan：返回句柄与计划 JSON（`route` / `estimatedSize`）。失败时不产出句柄。
#[no_mangle]
pub extern "C" fn logcore_plan(
    request: *const c_char,
    out_handle: *mut *mut SourceHandle,
    out_plan: *mut *mut c_char,
) -> i32 {
    guard(|| {
        if out_handle.is_null() || out_plan.is_null() {
            return record(CoreError::invalid_arg("out"));
        }
        unsafe {
            *out_handle = std::ptr::null_mut();
            *out_plan = std::ptr::null_mut();
        }
        let result = (|| -> CoreResult<(*mut SourceHandle, *mut c_char)> {
            let text = unsafe { request_text(request)? };
            let handle = SourceHandle::plan(text)?;
            let plan = into_c_string(&handle.plan_json());
            Ok((Box::into_raw(handle), plan))
        })();
        match result {
            Ok((handle, plan)) => {
                unsafe {
                    *out_handle = handle;
                    *out_plan = plan;
                }
                OK
            }
            Err(e) => record(e),
        }
    })
}

/// commit：复用 plan 句柄；受理即返，装载结果经事件上报（D24）。
#[no_mangle]
pub extern "C" fn logcore_commit(handle: *mut SourceHandle) -> i32 {
    guard(|| match unsafe { handle.as_ref() } {
        Some(h) => finish(h.commit()),
        None => record(CoreError::invalid_arg("handle")),
    })
}

/// feed：行数组深拷贝后入队即返；超限整批拒绝（`field = "lines"`）。
#[no_mangle]
pub extern "C" fn logcore_feed(
    handle: *mut SourceHandle,
    lines: *const LogcoreLine,
    count: usize,
) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        if count > 0 && lines.is_null() {
            return record(CoreError::invalid_arg("lines"));
        }
        let mut owned: Vec<Vec<u8>> = Vec::with_capacity(count);
        for i in 0..count {
            let line = unsafe { &*lines.add(i) };
            if line.len == 0 {
                owned.push(Vec::new());
                continue;
            }
            if line.data.is_null() {
                return record(CoreError::invalid_arg("lines"));
            }
            owned.push(unsafe { std::slice::from_raw_parts(line.data, line.len) }.to_vec());
        }
        finish(h.feed(owned))
    })
}

#[no_mangle]
pub extern "C" fn logcore_stop(handle: *mut SourceHandle) -> i32 {
    guard(|| match unsafe { handle.as_ref() } {
        Some(h) => finish(h.stop()),
        None => record(CoreError::invalid_arg("handle")),
    })
}

#[no_mangle]
pub extern "C" fn logcore_close(handle: *mut SourceHandle) -> i32 {
    guard(|| match unsafe { handle.as_ref() } {
        Some(h) => finish(h.close()),
        None => record(CoreError::invalid_arg("handle")),
    })
}

#[no_mangle]
pub extern "C" fn logcore_cancel(handle: *mut SourceHandle) -> i32 {
    guard(|| match unsafe { handle.as_ref() } {
        Some(h) => finish(h.cancel()),
        None => record(CoreError::invalid_arg("handle")),
    })
}

/// 一次取走全部待处理事件（JSON 数组字符串；空数组合法）。
#[no_mangle]
pub extern "C" fn logcore_poll_events(handle: *mut SourceHandle, out_json: *mut *mut c_char) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        match h.poll_events() {
            Ok(events) => match write_json_out(out_json, &events_json(&events)) {
                Ok(()) => OK,
                Err(e) => record(e),
            },
            Err(e) => record(e),
        }
    })
}

#[no_mangle]
pub extern "C" fn logcore_get_progress(handle: *mut SourceHandle, out_json: *mut *mut c_char) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        match h.progress_json() {
            Ok(value) => match write_json_out(out_json, &value) {
                Ok(()) => OK,
                Err(e) => record(e),
            },
            Err(e) => record(e),
        }
    })
}

#[no_mangle]
pub extern "C" fn logcore_get_descriptor(
    handle: *mut SourceHandle,
    out_json: *mut *mut c_char,
) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        match h.descriptor_json() {
            Ok(value) => match write_json_out(out_json, &value) {
                Ok(()) => OK,
                Err(e) => record(e),
            },
            Err(e) => record(e),
        }
    })
}

/// 行数：仅 `Loaded` 合法；状态门禁先于 out 指针校验。
#[no_mangle]
pub extern "C" fn logcore_row_count(handle: *mut SourceHandle, out_rows: *mut u64) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        if let Err(e) = state::check(h.state(), Op::RowCount) {
            return record(e);
        }
        if out_rows.is_null() {
            return record(CoreError::invalid_arg("out"));
        }
        match h.row_count() {
            Ok(rows) => {
                unsafe { *out_rows = rows };
                OK
            }
            Err(e) => record(e),
        }
    })
}

/// 取页：返回 core 分配的布局 v1 缓冲，宿主以 `logcore_free_page(ptr, len)` 释放。
#[no_mangle]
pub extern "C" fn logcore_page(
    handle: *mut SourceHandle,
    start: i64,
    count: i64,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        let Some(h) = (unsafe { handle.as_ref() }) else {
            return record(CoreError::invalid_arg("handle"));
        };
        if let Err(e) = state::check(h.state(), Op::Page) {
            return record(e);
        }
        // 失败路径不留可释放的 out 值；out 指针为 null 的调用无法写。
        if !out_ptr.is_null() {
            unsafe { *out_ptr = std::ptr::null_mut() };
        }
        if !out_len.is_null() {
            unsafe { *out_len = 0 };
        }
        if out_ptr.is_null() || out_len.is_null() {
            return record(CoreError::invalid_arg("out"));
        }
        match h.page(start, count) {
            Ok(bytes) => {
                let boxed = bytes.into_boxed_slice();
                let len = boxed.len();
                let ptr = Box::into_raw(boxed) as *mut u8;
                unsafe {
                    *out_ptr = ptr;
                    *out_len = len;
                }
                OK
            }
            Err(e) => record(e),
        }
    })
}

/// 释放页缓冲：按 `len` 重建 `Box<[u8]>`（分配时长度即容量）；null 安全。
#[no_mangle]
pub extern "C" fn logcore_free_page(ptr: *mut u8, len: usize) {
    let _ = guard(|| {
        if ptr.is_null() {
            return OK;
        }
        let slice = std::ptr::slice_from_raw_parts_mut(ptr, len);
        drop(unsafe { Box::from_raw(slice) });
        OK
    });
}

/// 启动维护：无句柄、不依赖活动源（D19）。
#[no_mangle]
pub extern "C" fn logcore_startup_maintain(
    request: *const c_char,
    out_json: *mut *mut c_char,
) -> i32 {
    guard(|| {
        let result = (|| -> CoreResult<Value> {
            let text = unsafe { request_text(request)? };
            maintain::maintain(text)
        })();
        match result {
            Ok(value) => match write_json_out(out_json, &value) {
                Ok(()) => OK,
                Err(e) => record(e),
            },
            Err(e) => record(e),
        }
    })
}

/// 结束全部 core 拉起的代理并清空登记（不关闭源、不释放句柄，幂等）。
#[no_mangle]
pub extern "C" fn logcore_shutdown() -> i32 {
    guard(|| {
        proxy::shutdown();
        OK
    })
}

/// 句柄释放：仅已关闭时合法；未关闭返回 `INVALID_STATE` 且不释放。
#[no_mangle]
pub extern "C" fn logcore_free_handle(handle: *mut SourceHandle) -> i32 {
    guard(|| {
        let state = match unsafe { handle.as_ref() } {
            Some(h) => h.state(),
            None => return record(CoreError::invalid_arg("handle")),
        };
        if state != State::Closed {
            return record(CoreError::new(INVALID_STATE));
        }
        drop(unsafe { Box::from_raw(handle) });
        OK
    })
}

#[no_mangle]
pub extern "C" fn logcore_free_string(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    drop(unsafe { CString::from_raw(ptr) });
}

/// 取走线程本地最近一次失败（`{code, params}`），无失败返回 `null`。
#[no_mangle]
pub extern "C" fn logcore_take_last_error(out_json: *mut *mut c_char) -> i32 {
    guard(|| {
        if out_json.is_null() {
            return record(CoreError::invalid_arg("out"));
        }
        let err = LAST_ERROR.with(|slot| slot.borrow_mut().take());
        let value = match err {
            Some(e) => e.to_json(),
            None => Value::Null,
        };
        unsafe { *out_json = into_c_string(&value) };
        OK
    })
}

#[cfg(test)]
#[no_mangle]
pub extern "C" fn logcore_test_panic() -> i32 {
    guard(|| panic!("测试 panic：应被 catch_unwind 转成 INTERNAL"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use logcore_core::error::CLOSED;
    use logcore_core::testutil::{file_request, temp_dir, text_file, wait_until};
    use std::ffi::CString;

    fn take(out: *mut c_char) -> Value {
        let text = unsafe { CStr::from_ptr(out) }.to_str().unwrap().to_string();
        logcore_free_string(out);
        serde_json::from_str(&text).unwrap()
    }

    fn take_error() -> Value {
        let mut out: *mut c_char = std::ptr::null_mut();
        assert_eq!(logcore_take_last_error(&mut out), OK);
        take(out)
    }

    fn plan(request: &str) -> (i32, *mut SourceHandle, Value) {
        let text = CString::new(request).unwrap();
        let mut handle: *mut SourceHandle = std::ptr::null_mut();
        let mut plan: *mut c_char = std::ptr::null_mut();
        let code = logcore_plan(text.as_ptr(), &mut handle, &mut plan);
        let value = if plan.is_null() { Value::Null } else { take(plan) };
        (code, handle, value)
    }

    fn read_json(code: i32, ptr: *mut c_char) -> Value {
        assert_eq!(code, OK);
        take(ptr)
    }

    fn descriptor(handle: *mut SourceHandle) -> Value {
        let mut out: *mut c_char = std::ptr::null_mut();
        let code = logcore_get_descriptor(handle, &mut out);
        read_json(code, out)
    }

    fn progress(handle: *mut SourceHandle) -> Value {
        let mut out: *mut c_char = std::ptr::null_mut();
        let code = logcore_get_progress(handle, &mut out);
        read_json(code, out)
    }

    fn poll(handle: *mut SourceHandle) -> Value {
        let mut out: *mut c_char = std::ptr::null_mut();
        let code = logcore_poll_events(handle, &mut out);
        read_json(code, out)
    }

    fn handle_ref(handle: *mut SourceHandle) -> &'static SourceHandle {
        unsafe { &*handle }
    }

    #[test]
    fn ffi_string_roundtrip() {
        let dir = temp_dir("ffi-roundtrip");
        let file = text_file(&dir, "a.log", &["11-14 12:00:01.123  1  2 I Tag: hello"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-a", "", &[], 500, "Auto");
        let (code, handle, plan) = plan(&req);
        assert_eq!(code, OK);
        assert_eq!(plan["route"], "LocalDirect");
        assert_eq!(plan["estimatedSize"].as_u64(), Some(std::fs::metadata(&file).unwrap().len()));
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded), "state={:?}", handle_ref(handle).state());
        let desc = descriptor(handle);
        assert_eq!(desc["rowCount"], 1);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn ffi_panic_maps_to_error_code() {
        assert_eq!(logcore_test_panic(), INTERNAL);
        assert_eq!(take_error()["code"], INTERNAL);
    }

    #[test]
    fn plan_handle_reused_by_commit() {
        let dir = temp_dir("ffi-reuse");
        let file = text_file(&dir, "b.log", &["11-14 12:00:01.123  1  2 I Tag: x"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-b", "", &[], 500, "Auto");
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        assert!(!handle.is_null());
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn plan_failure_returns_error_without_event() {
        let dir = temp_dir("ffi-plan-fail");
        let missing = dir.join("missing.log");
        let req = file_request(&missing, &dir, &dir, "logcore-ffi-c", "", &[], 500, "Auto");
        let (code, handle, plan) = plan(&req);
        assert_eq!(code, 13);
        assert!(handle.is_null(), "plan 失败不建立句柄");
        assert!(plan.is_null());
        let err = take_error();
        assert_eq!(err["code"], 13);
        assert_eq!(err["params"]["path"], missing.to_string_lossy().as_ref());
    }

    #[test]
    fn commit_returns_before_load_completes() {
        let dir = temp_dir("ffi-async");
        let file = text_file(&dir, "big.log", &["11-14 12:00:01.123  1  2 I Tag: x"; 2000]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-d", "", &[], 500, "Auto");
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        handle_ref(handle).hold_loader();
        assert_eq!(logcore_commit(handle), OK);
        assert_eq!(handle_ref(handle).state(), State::Committing, "commit 受理即返");
        assert_eq!(descriptor(handle)["rowCount"], 0);
        handle_ref(handle).release_loader();
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn get_descriptor_reads_current_values() {
        let dir = temp_dir("ffi-desc-current");
        let file = text_file(&dir, "c.log", &["11-14 12:00:01.123  1  2 I Tag: x"; 3]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-e", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        let desc = descriptor(handle);
        assert_eq!(desc["rowCount"], 3);
        assert_eq!(desc["docSize"].as_u64(), Some(std::fs::metadata(&file).unwrap().len()));
        assert_eq!(desc["channel"], "Local");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn descriptor_skeleton_on_commit_accept() {
        let dir = temp_dir("ffi-desc-skeleton");
        let file = text_file(&dir, "d.log", &["11-14 12:00:01.123  1  2 I Tag: x"; 100]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-f", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        handle_ref(handle).hold_loader();
        assert_eq!(logcore_commit(handle), OK);
        let desc = descriptor(handle);
        assert_eq!(desc["rowCount"], 0);
        assert_eq!(desc["channel"], "Local");
        assert_eq!(desc["loadMode"], "Progressive");
        assert!(desc["identity"].is_object(), "身份取 plan 阶段确定值");
        handle_ref(handle).release_loader();
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn descriptor_readable_after_failure() {
        let dir = temp_dir("ffi-desc-failed");
        let file = text_file(&dir, "e.log", &["11-14 12:00:01.123  1  2 I Tag: x"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-g", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        std::fs::remove_file(&file).unwrap();
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Failed));
        let desc = descriptor(handle);
        assert_eq!(desc["channel"], "Local");
        assert_eq!(desc["rowCount"], 0);
        let events = poll(handle);
        let err = events
            .as_array()
            .unwrap()
            .iter()
            .find(|e| e["type"] == "error")
            .expect("失败经错误事件上报");
        assert_eq!(err["code"], 12);
        assert_eq!(err["params"]["path"], file.to_string_lossy().as_ref());
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn close_keeps_handle_until_free() {
        let dir = temp_dir("ffi-close-free");
        let file = text_file(&dir, "f.log", &["11-14 12:00:01.123  1  2 I Tag: x"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-h", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        assert_eq!(logcore_free_handle(handle), INVALID_STATE, "未关闭不得释放");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(handle_ref(handle).state(), State::Closed);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn state_free_before_close_invalid() {
        let dir = temp_dir("ffi-free-before-close");
        let file = text_file(&dir, "h.log", &["11-14 12:00:01.123  1  2 I Tag: x"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-j", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        let p = progress(handle);
        assert_eq!(
            p["value"].as_u64(),
            Some(std::fs::metadata(&file).unwrap().len()),
            "装载完成后进度值等于已索引字节数"
        );
        assert_eq!(logcore_free_handle(handle), INVALID_STATE, "Loaded 不得释放");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn state_free_during_stopping_invalid() {
        let dir = temp_dir("ffi-free-stopping");
        let req = logcore_core::testutil::device_request(&dir);
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        assert_eq!(logcore_commit(handle), OK);
        handle_ref(handle).set_slow_consume(true);
        for i in 0..30 {
            let lines: Vec<Vec<u8>> = (0..10)
                .map(|j| format!("11-14 12:00:01.123  1  2 I Tag: {i}-{j}").into_bytes())
                .collect();
            let raw: Vec<LogcoreLine> = lines
                .iter()
                .map(|l| LogcoreLine {
                    data: l.as_ptr(),
                    len: l.len(),
                })
                .collect();
            assert_eq!(logcore_feed(handle, raw.as_ptr(), raw.len()), OK);
        }
        let addr = handle as usize;
        let stop = std::thread::spawn(move || logcore_stop(addr as *mut SourceHandle));
        assert!(
            wait_until(|| handle_ref(handle).state() == State::Stopping),
            "未进入 Stopping，当前 {:?}",
            handle_ref(handle).state()
        );
        assert_eq!(logcore_free_handle(handle), INVALID_STATE, "Stopping 不得释放");
        assert_eq!(stop.join().unwrap(), OK);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn poll_after_close_returns_closed() {
        let dir = temp_dir("ffi-poll-closed");
        let file = text_file(&dir, "g.log", &["11-14 12:00:01.123  1  2 I Tag: x"]);
        let req = file_request(&file, &dir, &dir, "logcore-ffi-i", "", &[], 500, "Auto");
        let (_, handle, _) = plan(&req);
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        let mut out: *mut c_char = std::ptr::null_mut();
        assert_eq!(logcore_poll_events(handle, &mut out), CLOSED);
        assert!(out.is_null());
        let mut out: *mut c_char = std::ptr::null_mut();
        assert_eq!(logcore_get_progress(handle, &mut out), CLOSED);
        let mut out: *mut c_char = std::ptr::null_mut();
        assert_eq!(logcore_get_descriptor(handle, &mut out), CLOSED);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn feed_copies_lines_before_return() {
        let dir = temp_dir("ffi-feed-copy");
        let req = logcore_core::testutil::device_request(&dir);
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        assert_eq!(logcore_commit(handle), OK);
        let path = std::path::PathBuf::from(descriptor(handle)["sourcePath"].as_str().unwrap());

        let mut first = b"11-14 12:00:01.123  1  2 I Tag: one".to_vec();
        let line = LogcoreLine {
            data: first.as_ptr(),
            len: first.len(),
        };
        assert_eq!(logcore_feed(handle, &line, 1), OK);
        // 返回后立刻覆盖宿主缓冲：落盘内容不受影响
        for b in first.iter_mut() {
            *b = b'Z';
        }
        assert!(wait_until(|| {
            std::fs::read(&path)
                .map(|b| b.windows(3).any(|w| w == b"one"))
                .unwrap_or(false)
        }));
        assert_eq!(logcore_stop(handle), OK);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    fn loaded_handle(req: &str) -> *mut SourceHandle {
        let (code, handle, _) = plan(req);
        assert_eq!(code, OK);
        assert_eq!(logcore_commit(handle), OK);
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        handle
    }

    fn header_rows(ptr: *const u8) -> u32 {
        let bytes = unsafe { std::slice::from_raw_parts(ptr, 36) };
        assert_eq!(&bytes[..4], b"LPAG");
        u32::from_le_bytes(bytes[8..12].try_into().unwrap())
    }

    fn page_call(handle: *mut SourceHandle, start: i64, count: i64) -> (i32, *mut u8, usize) {
        let mut ptr: *mut u8 = std::ptr::null_mut();
        let mut len = 0usize;
        let code = logcore_page(handle, start, count, &mut ptr, &mut len);
        (code, ptr, len)
    }

    fn paging_fixture(tag: &str) -> (std::path::PathBuf, std::path::PathBuf) {
        let dir = temp_dir(tag);
        let file = text_file(
            &dir,
            "p.log",
            &[
                "11-14 12:00:01.123  1  2 I Tag: a",
                "11-14 12:00:02.123  3  4 D Tag: b",
                "11-14 12:00:03.123  5  6 E Tag: c",
            ],
        );
        (dir, file)
    }

    #[test]
    fn cabi_row_count_matches_descriptor() {
        let (dir, file) = paging_fixture("ffi-rowcount");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-rc", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let mut rows = 0u64;
        assert_eq!(logcore_row_count(handle, &mut rows), OK);
        assert_eq!(rows, 3);
        assert_eq!(descriptor(handle)["rowCount"].as_u64(), Some(3));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_row_count_before_loaded_invalid() {
        let (dir, file) = paging_fixture("ffi-rowcount-gate");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-rcg", "", &[], 500, "Auto");
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        handle_ref(handle).hold_loader();
        assert_eq!(logcore_commit(handle), OK);
        let mut rows = 0u64;
        assert_eq!(logcore_row_count(handle, &mut rows), INVALID_STATE);
        handle_ref(handle).release_loader();
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_row_count_null_out_invalid() {
        let (dir, file) = paging_fixture("ffi-rowcount-null");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-rcn", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        assert_eq!(
            logcore_row_count(handle, std::ptr::null_mut()),
            logcore_core::error::INVALID_ARG
        );
        assert_eq!(take_error()["params"]["field"], "out");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_beyond_rows_returns_empty() {
        let (dir, file) = paging_fixture("ffi-page-beyond");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pb", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, ptr, len) = page_call(handle, 3, 5);
        assert_eq!(code, OK);
        assert_eq!(len, 36);
        assert_eq!(header_rows(ptr), 0);
        logcore_free_page(ptr, len);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_zero_count_returns_header_only() {
        let (dir, file) = paging_fixture("ffi-page-zero");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pz", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, ptr, len) = page_call(handle, 0, 0);
        assert_eq!(code, OK);
        assert_eq!(len, 36);
        assert_eq!(header_rows(ptr), 0);
        logcore_free_page(ptr, len);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_count_over_max_field() {
        let (dir, file) = paging_fixture("ffi-page-max");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pm", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, ptr, len) = page_call(handle, 0, 4097);
        assert_eq!(code, logcore_core::error::INVALID_ARG);
        assert_eq!(take_error()["params"]["field"], "count");
        assert!(ptr.is_null() && len == 0);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_negative_start_field() {
        let (dir, file) = paging_fixture("ffi-page-negstart");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pns", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, _, _) = page_call(handle, -1, 1);
        assert_eq!(code, logcore_core::error::INVALID_ARG);
        assert_eq!(take_error()["params"]["field"], "start");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_negative_count_field() {
        let (dir, file) = paging_fixture("ffi-page-negcount");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pnc", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, _, _) = page_call(handle, 0, -1);
        assert_eq!(code, logcore_core::error::INVALID_ARG);
        assert_eq!(take_error()["params"]["field"], "count");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_invalid_state_beats_param_error() {
        let (dir, file) = paging_fixture("ffi-page-state");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pst", "", &[], 500, "Auto");
        let (code, handle, _) = plan(&req);
        assert_eq!(code, OK);
        handle_ref(handle).hold_loader();
        assert_eq!(logcore_commit(handle), OK);
        let (code, _, _) = page_call(handle, -1, -1);
        assert_eq!(code, INVALID_STATE, "非法状态先于参数校验");
        handle_ref(handle).release_loader();
        assert!(wait_until(|| handle_ref(handle).state() == State::Loaded));
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_null_out_invalid() {
        let (dir, file) = paging_fixture("ffi-page-nullout");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pno", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let mut len = 99usize;
        let code = logcore_page(handle, 0, 1, std::ptr::null_mut(), &mut len);
        assert_eq!(code, logcore_core::error::INVALID_ARG);
        assert_eq!(take_error()["params"]["field"], "out");
        assert_eq!(len, 0, "失败路径清空长度");
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_error_clears_out() {
        let (dir, file) = paging_fixture("ffi-page-clear");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pcl", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let mut ptr: *mut u8 = 1 as *mut u8;
        let mut len = 7usize;
        let code = logcore_page(handle, 0, 5000, &mut ptr, &mut len);
        assert_eq!(code, logcore_core::error::INVALID_ARG);
        assert!(ptr.is_null());
        assert_eq!(len, 0);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_page_start_near_i64_max_returns_empty() {
        let (dir, file) = paging_fixture("ffi-page-imax");
        let req = file_request(&file, &dir, &dir, "logcore-ffi-pim", "", &[], 500, "Auto");
        let handle = loaded_handle(&req);
        let (code, ptr, len) = page_call(handle, i64::MAX, 5);
        assert_eq!(code, OK, "起点饱和后为空页而非溢出");
        assert_eq!(len, 36);
        assert_eq!(header_rows(ptr), 0);
        logcore_free_page(ptr, len);
        assert_eq!(logcore_close(handle), OK);
        assert_eq!(logcore_free_handle(handle), OK);
    }

    #[test]
    fn cabi_free_page_null_safe() {
        logcore_free_page(std::ptr::null_mut(), 0);
        logcore_free_page(std::ptr::null_mut(), 36);
    }
}