//! 临时产物锁标记（D7）：先以独占方式打开 `<产物>.lock`，再创建产物。
//!
//! Windows 上 Rust 默认的文件共享模式允许其他打开者，因此这里显式取
//! `share_mode(0)`，让「能否再次独占打开」成为存活判定（D7 的维护口径）。

use std::fs::{File, OpenOptions};
use std::io;
use std::path::{Path, PathBuf};

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

pub fn marker_path(artifact: &Path) -> PathBuf {
    let mut name = artifact.as_os_str().to_owned();
    name.push(".lock");
    PathBuf::from(name)
}

fn exclusive_open(path: &Path, create_new: bool) -> io::Result<File> {
    let mut opts = OpenOptions::new();
    opts.write(true).create_new(create_new);
    #[cfg(windows)]
    opts.share_mode(0);
    opts.open(path)
}

/// 先取标记再建产物；标记已存在表示有活动持有者。
pub fn claim(artifact: &Path) -> io::Result<File> {
    exclusive_open(&marker_path(artifact), true)
}

/// 维护判定：能独占取得现有标记即视为无主。
pub fn try_take(marker: &Path) -> io::Result<File> {
    exclusive_open(marker, false)
}