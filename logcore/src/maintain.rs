//! 启动维护入口 `startup_maintain`（design D19）：无句柄、不依赖活动源。
//!
//! 缓存侧：结构校验、删除损坏条目与写入残留、按容量回收（以空在用集合运行）。
//! 临时目录侧：按 D7 命名识别暂存目录与采集文件，逐个尝试独占 `.lock` 标记决定
//! 无主与否。幂等；任一侧失败返回 `Partial` 而不抛错。

use std::fs;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use serde_json::{json, Value};

use crate::error::{CoreError, CoreResult};
use crate::locks;
use crate::request::is_absolute_path;
use crate::sidecar::{self, EXTENSION};

const TEMP_MARK: &str = ".tmp-";
const SPOOL_PREFIX: &str = "logcat_spool_";
const LIVE_PREFIX: &str = "logcat_live_";
const LIVE_EXT: &str = ".log";
const LOCK_EXT: &str = ".lock";
const DEFAULT_ENTRY_LIMIT: u64 = 1 << 30;
const DEFAULT_DIR_LIMIT: u64 = 4 << 30;

struct Stats {
    removed_corrupt: u64,
    removed_temp_writes: u64,
    removed_orphans: u64,
    skipped_in_use: u64,
    evicted: u64,
    kept: u64,
    freed_bytes: u64,
    partial: bool,
}

impl Stats {
    fn new() -> Self {
        Self {
            removed_corrupt: 0,
            removed_temp_writes: 0,
            removed_orphans: 0,
            skipped_in_use: 0,
            evicted: 0,
            kept: 0,
            freed_bytes: 0,
            partial: false,
        }
    }

    fn to_json(&self) -> Value {
        json!({
            "status": if self.partial { "Partial" } else { "Ok" },
            "removedCorrupt": self.removed_corrupt,
            "removedTempWrites": self.removed_temp_writes,
            "removedOrphans": self.removed_orphans,
            "skippedInUse": self.skipped_in_use,
            "evicted": self.evicted,
            "kept": self.kept,
            "freedBytes": self.freed_bytes,
        })
    }
}

/// 解析请求并执行维护。请求字段：`{ cacheDir, tempDir, entryLimitBytes?, dirLimitBytes? }`。
pub fn maintain(request_json: &str) -> CoreResult<Value> {
    let root: Value = serde_json::from_str(request_json)
        .map_err(|_| CoreError::invalid_arg("request"))?;
    let obj = root
        .as_object()
        .ok_or_else(|| CoreError::invalid_arg("request"))?;
    let cache_dir = required_dir(obj.get("cacheDir"), "cacheDir")?;
    let temp_dir = required_dir(obj.get("tempDir"), "tempDir")?;
    let entry_limit = optional_limit(obj.get("entryLimitBytes"), "entryLimitBytes", DEFAULT_ENTRY_LIMIT)?;
    let dir_limit = optional_limit(obj.get("dirLimitBytes"), "dirLimitBytes", DEFAULT_DIR_LIMIT)?;
    Ok(run(Path::new(&cache_dir), Path::new(&temp_dir), entry_limit, dir_limit).to_json())
}

fn required_dir(value: Option<&Value>, field: &str) -> CoreResult<String> {
    let s = value
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty() && is_absolute_path(s))
        .ok_or_else(|| CoreError::invalid_arg(field))?;
    Ok(s.to_string())
}

fn optional_limit(value: Option<&Value>, field: &str, default: u64) -> CoreResult<u64> {
    match value {
        None => Ok(default),
        Some(v) => {
            let n = v.as_u64().ok_or_else(|| CoreError::invalid_arg(field))?;
            if n == 0 {
                return Err(CoreError::invalid_arg(field));
            }
            Ok(n)
        }
    }
}

fn run(cache_dir: &Path, temp_dir: &Path, entry_limit: u64, dir_limit: u64) -> Stats {
    let mut stats = Stats::new();
    maintain_cache(cache_dir, entry_limit, dir_limit, &mut stats);
    maintain_temp(temp_dir, &mut stats);
    stats
}

// ── 缓存侧 ──

fn maintain_cache(dir: &Path, entry_limit: u64, dir_limit: u64, stats: &mut Stats) {
    let read = match fs::read_dir(dir) {
        Ok(read) => read,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return,
        Err(_) => {
            stats.partial = true;
            return;
        }
    };

    let mut valid: Vec<(PathBuf, SystemTime, u64)> = Vec::new();
    for entry in read.flatten() {
        let path = entry.path();
        let name = entry.file_name().to_string_lossy().to_string();
        let Ok(meta) = entry.metadata() else {
            stats.partial = true;
            continue;
        };
        if !meta.is_file() {
            continue;
        }
        if name.contains(TEMP_MARK) {
            if remove_file(&path) {
                stats.removed_temp_writes += 1;
                stats.freed_bytes += meta.len();
            } else {
                stats.partial = true;
            }
            continue;
        }
        if path.extension().and_then(|s| s.to_str()) != Some(&EXTENSION[1..]) {
            continue;
        }
        let bytes = match fs::read(&path) {
            Ok(b) => b,
            Err(_) => {
                stats.partial = true;
                continue;
            }
        };
        let len = bytes.len() as u64;
        if sidecar::structurally_readable(&bytes) {
            if len > entry_limit {
                if remove_file(&path) {
                    stats.evicted += 1;
                    stats.freed_bytes += len;
                } else {
                    stats.partial = true;
                }
                continue;
            }
            valid.push((path, last_used(&meta), len));
        } else if remove_file(&path) {
            stats.removed_corrupt += 1;
            stats.freed_bytes += len;
        } else {
            stats.partial = true;
        }
    }

    let mut total: u64 = valid.iter().map(|e| e.2).sum();
    let mut evicted_valid = 0u64;
    if total > dir_limit {
        valid.sort_by_key(|e| e.1);
        for (path, _, len) in &valid {
            if total <= dir_limit {
                break;
            }
            if remove_file(path) {
                stats.evicted += 1;
                evicted_valid += 1;
                stats.freed_bytes += len;
                total = total.saturating_sub(*len);
            } else {
                stats.partial = true;
            }
        }
    }
    stats.kept = valid.len() as u64 - evicted_valid;
}

// ── 临时目录侧 ──

fn maintain_temp(dir: &Path, stats: &mut Stats) {
    let read = match fs::read_dir(dir) {
        Ok(read) => read,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return,
        Err(_) => {
            stats.partial = true;
            return;
        }
    };

    let mut entries: Vec<(String, PathBuf, fs::Metadata)> = Vec::new();
    for entry in read.flatten() {
        let Ok(meta) = entry.metadata() else {
            stats.partial = true;
            continue;
        };
        entries.push((
            entry.file_name().to_string_lossy().to_string(),
            entry.path(),
            meta,
        ));
    }

    let mut markers_without_artifact = Vec::new();
    for (name, path, meta) in &entries {
        if let Some(artifact) = marker_owner(name) {
            let artifact_present = entries.iter().any(|(n, _, _)| n == &artifact);
            if !artifact_present {
                markers_without_artifact.push(path.clone());
            }
            continue;
        }
        if is_spool_dir(name, meta) || is_capture_file(name, meta) {
            match locks::try_take(&locks::marker_path(path)) {
                Ok(held) => {
                    drop(held);
                    let size = artifact_size(path);
                    if remove_artifact(path) {
                        let _ = fs::remove_file(locks::marker_path(path));
                        stats.removed_orphans += 1;
                        stats.freed_bytes += size;
                    } else {
                        stats.partial = true;
                    }
                }
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                    // 产物在而标记缺失：无主
                    let size = artifact_size(path);
                    if remove_artifact(path) {
                        stats.removed_orphans += 1;
                        stats.freed_bytes += size;
                    } else {
                        stats.partial = true;
                    }
                }
                Err(_) => stats.skipped_in_use += 1,
            }
        }
    }

    for marker in markers_without_artifact {
        match locks::try_take(&marker) {
            Ok(held) => {
                drop(held);
                if remove_file(&marker) {
                    stats.removed_temp_writes += 1;
                } else {
                    stats.partial = true;
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => stats.skipped_in_use += 1,
        }
    }
}

/// `.lock` 标记名 → 对应产物名。
fn marker_owner(name: &str) -> Option<String> {
    let base = name.strip_suffix(LOCK_EXT)?;
    if is_spool_name(base) || is_live_name(base) {
        Some(base.to_string())
    } else {
        None
    }
}

fn is_spool_name(name: &str) -> bool {
    name.strip_prefix(SPOOL_PREFIX)
        .map(|rest| !rest.is_empty() && rest.chars().all(|c| c.is_ascii_digit()))
        .unwrap_or(false)
}

fn is_live_name(name: &str) -> bool {
    let Some(rest) = name.strip_prefix(LIVE_PREFIX) else {
        return false;
    };
    let Some(stem) = rest.strip_suffix(LIVE_EXT) else {
        return false;
    };
    let mut parts = stem.split('_');
    let (Some(date), Some(time), Some(seq), None) = (parts.next(), parts.next(), parts.next(), parts.next()) else {
        return false;
    };
    date.len() == 8
        && date.chars().all(|c| c.is_ascii_digit())
        && time.len() == 6
        && time.chars().all(|c| c.is_ascii_digit())
        && !seq.is_empty()
        && seq.chars().all(|c| c.is_ascii_digit())
}

fn is_spool_dir(name: &str, meta: &fs::Metadata) -> bool {
    meta.is_dir() && is_spool_name(name)
}

fn is_capture_file(name: &str, meta: &fs::Metadata) -> bool {
    meta.is_file() && is_live_name(name)
}

fn remove_artifact(path: &Path) -> bool {
    if path.is_dir() {
        fs::remove_dir_all(path).is_ok()
    } else {
        remove_file(path)
    }
}

fn artifact_size(path: &Path) -> u64 {
    if path.is_file() {
        return fs::metadata(path).map(|m| m.len()).unwrap_or(0);
    }
    let Ok(read) = fs::read_dir(path) else {
        return 0;
    };
    read.flatten()
        .map(|e| artifact_size(&e.path()))
        .sum()
}

fn remove_file(path: &Path) -> bool {
    fs::remove_file(path).is_ok()
}

fn last_used(meta: &fs::Metadata) -> SystemTime {
    match (meta.accessed(), meta.modified()) {
        (Ok(a), Ok(m)) => {
            if a < m {
                m
            } else {
                a
            }
        }
        (Ok(a), _) => a,
        (_, Ok(m)) => m,
        _ => SystemTime::UNIX_EPOCH,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::INVALID_ARG;
    use crate::identity::{Identity, FILETIME_UNIX_EPOCH_TICKS};
    use crate::locks;
    use crate::sidecar::tests::{sample_payload, temp_dir};
    use crate::sidecar::{encode, CacheStore, FORMAT_VERSION};

    fn id() -> Identity {
        Identity {
            size: 10,
            mtime_ticks: FILETIME_UNIX_EPOCH_TICKS + 1,
            head_hash: 1,
            tail_hash: 2,
        }
    }

    fn long_path(p: &Path) -> String {
        p.to_string_lossy().to_string()
    }

    fn request(cache: &Path, temp: &Path) -> String {
        json!({
            "cacheDir": long_path(cache),
            "tempDir": long_path(temp),
        })
        .to_string()
    }

    fn write_entry(dir: &Path, name: &str, bytes: &[u8]) {
        fs::write(dir.join(name), bytes).unwrap();
    }

    #[test]
    fn maintain_cleans_cache_corrupt_and_temp_writes() {
        let cache = temp_dir("maint-clean-cache");
        let temp = temp_dir("maint-clean-temp");
        let valid = encode(&id(), &sample_payload(), FORMAT_VERSION);
        write_entry(&cache, "good_0123456789abcdef.lcs", &valid);
        write_entry(&cache, "bad_0123456789abcdef.lcs", b"garbage");
        write_entry(&cache, "temp_0123456789abcdef.lcs.tmp-42", b"half");
        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.removed_corrupt, 1);
        assert_eq!(stats.removed_temp_writes, 1);
        assert_eq!(stats.kept, 1);
        assert!(!stats.partial);
        assert!(!cache.join("bad_0123456789abcdef.lcs").exists());
        assert!(!cache.join("temp_0123456789abcdef.lcs.tmp-42").exists());
        assert!(cache.join("good_0123456789abcdef.lcs").exists());
    }

    #[test]
    fn maintain_cleans_orphan_temp_artifacts() {
        let cache = temp_dir("maint-orphan-cache");
        let temp = temp_dir("maint-orphan-temp");
        let spool = temp.join("logcat_spool_7");
        fs::create_dir_all(&spool).unwrap();
        fs::write(spool.join("a.log"), b"x").unwrap();
        let capture = temp.join("logcat_live_20261114_120000_8.log");
        fs::write(&capture, b"y").unwrap();
        // 崩溃残留：标记与产物同时存在但无人持有
        locks::claim(&spool).unwrap();
        locks::claim(&capture).unwrap();

        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.removed_orphans, 2);
        assert_eq!(stats.skipped_in_use, 0);
        assert!(!spool.exists());
        assert!(!locks::marker_path(&spool).exists());
        assert!(!capture.exists());
        assert!(!locks::marker_path(&capture).exists());
    }

    #[test]
    fn maintain_skips_artifacts_with_held_marker() {
        let cache = temp_dir("maint-held-cache");
        let temp = temp_dir("maint-held-temp");
        let capture = temp.join("logcat_live_20261114_120000_9.log");
        fs::write(&capture, b"live").unwrap();
        let held = locks::claim(&capture).unwrap();

        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.skipped_in_use, 1);
        assert_eq!(stats.removed_orphans, 0);
        assert!(capture.exists(), "仍被占用的产物不得删除");
        assert!(locks::marker_path(&capture).exists());

        drop(held);
        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.removed_orphans, 1);
        assert!(!capture.exists());
    }

    #[test]
    fn maintain_removes_orphan_lock_markers() {
        let cache = temp_dir("maint-marker-cache");
        let temp = temp_dir("maint-marker-temp");
        let spool_marker = temp.join("logcat_spool_11.lock");
        fs::write(&spool_marker, b"").unwrap();
        let live_marker = temp.join("logcat_live_20261114_120000_12.log.lock");
        fs::write(&live_marker, b"").unwrap();

        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.removed_temp_writes, 2);
        assert!(!spool_marker.exists());
        assert!(!live_marker.exists());
    }

    #[test]
    fn maintain_ignores_unmatched_entries() {
        let cache = temp_dir("maint-unmatched-cache");
        let temp = temp_dir("maint-unmatched-temp");
        let host_file = temp.join("notes.txt");
        fs::write(&host_file, b"keep").unwrap();
        let odd_dir = temp.join("logcat_spool_abc");
        fs::create_dir_all(&odd_dir).unwrap();
        let odd_capture = temp.join("logcat_live_2026_1.log");
        fs::write(&odd_capture, b"keep").unwrap();
        let odd_lock = temp.join("something.lock");
        fs::write(&odd_lock, b"").unwrap();

        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.removed_orphans, 0);
        assert_eq!(stats.removed_temp_writes, 0);
        assert!(host_file.exists(), "不匹配命名的条目一律不碰");
        assert!(odd_dir.exists());
        assert!(odd_capture.exists());
        assert!(odd_lock.exists());
    }

    #[test]
    fn maintain_is_idempotent() {
        let cache = temp_dir("maint-idem-cache");
        let temp = temp_dir("maint-idem-temp");
        let valid = encode(&id(), &sample_payload(), FORMAT_VERSION);
        write_entry(&cache, "good_0123456789abcdef.lcs", &valid);
        fs::write(temp.join("logcat_live_20261114_120000_2.log"), b"z").unwrap();
        let first = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        let second = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(first.removed_orphans, 1);
        assert_eq!(second.removed_orphans, 0);
        assert_eq!(second.removed_corrupt, 0);
        assert_eq!(second.removed_temp_writes, 0);
        assert_eq!(second.evicted, 0);
        assert_eq!(second.kept, 1);
    }

    #[test]
    fn maintain_missing_dirs_is_empty() {
        let base = temp_dir("maint-missing");
        let cache = base.join("no-cache");
        let temp = base.join("no-temp");
        let result = maintain(&request(&cache, &temp)).unwrap();
        assert_eq!(result["status"], "Ok");
        assert_eq!(result["kept"], 0);
        assert_eq!(result["freedBytes"], 0);
        assert!(!cache.exists(), "不得创建目录");
        assert!(!temp.exists(), "不得创建目录");
    }

    #[test]
    fn maintain_evicts_in_use_of_other_owner() {
        let cache = temp_dir("maint-evict-cache");
        let temp = temp_dir("maint-evict-temp");
        let payload = sample_payload();
        let entry_len = encode(&id(), &payload, FORMAT_VERSION).len() as u64;
        let store = CacheStore::new(cache.clone(), 1 << 30, entry_len * 2);
        let src = "C:\\logs\\shared.log".to_string();
        assert!(store.save(&src, Some(&id()), &id(), &payload));
        store.mark_in_use(&src);

        // 维护以空在用集合运行：其他所有者的在用条目可被回收
        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, entry_len / 2);
        assert_eq!(stats.evicted, 1);
        assert_eq!(stats.kept, 0);
        assert!(!store.entry_path(&src).exists());
        store.unmark_in_use(&src);
    }

    #[test]
    fn maintain_reports_kept_entries() {
        let cache = temp_dir("maint-kept-cache");
        let temp = temp_dir("maint-kept-temp");
        let valid = encode(&id(), &sample_payload(), FORMAT_VERSION);
        write_entry(&cache, "a_0123456789abcdef.lcs", &valid);
        write_entry(&cache, "b_0123456789abcdef.lcs", &valid);
        let stats = run(&cache, &temp, DEFAULT_ENTRY_LIMIT, DEFAULT_DIR_LIMIT);
        assert_eq!(stats.kept, 2);
        assert_eq!(stats.evicted, 0);
    }

    #[test]
    fn maintain_failure_is_partial() {
        let cache = temp_dir("maint-partial-cache");
        let temp = temp_dir("maint-partial-temp");
        let valid = encode(&id(), &sample_payload(), FORMAT_VERSION);
        write_entry(&cache, "good_0123456789abcdef.lcs", &valid);
        // 独占持有（共享模式 0）使删除失败：结构损坏但删不掉 → Partial，且不误删有效条目
        let stuck = cache.join("bad_0123456789abcdef.lcs");
        write_entry(&cache, "bad_0123456789abcdef.lcs", b"garbage");
        use std::os::windows::fs::OpenOptionsExt;
        let held = fs::OpenOptions::new()
            .read(true)
            .write(true)
            .share_mode(0)
            .open(&stuck)
            .unwrap();

        let result = maintain(&request(&cache, &temp)).unwrap();
        assert_eq!(result["status"], "Partial");
        assert_eq!(result["removedCorrupt"], 0);
        assert!(stuck.exists());
        assert!(cache.join("good_0123456789abcdef.lcs").exists(), "有效条目不得删除");

        drop(held);
    }

    #[test]
    fn maintain_rejects_bad_request() {
        let missing = serde_json::json!({ "tempDir": "C:\\t" });
        assert_eq!(maintain(&missing.to_string()).unwrap_err().code, INVALID_ARG);
        let relative = serde_json::json!({ "cacheDir": "cache", "tempDir": "C:\\t" });
        let err = maintain(&relative.to_string()).unwrap_err();
        assert_eq!(err.code, INVALID_ARG);
        assert_eq!(err.params["field"], "cacheDir");
    }
}