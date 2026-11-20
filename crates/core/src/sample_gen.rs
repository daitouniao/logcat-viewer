//! 确定性样本生成器（固定种子、显式字节）：只在测试 / 测试支持下编译。
//!
//! 运行时把样本写到调用方给定目录（测试用临时目录），仓库不入库大文件。
//! 大样本每 1000 行插入一个 4 行崩溃块，四种轮换：
//! Java / Native / ANR / Tombstone（Tombstone 块产出 2 个事件：头行 + 独立 signal 行）。

use std::fs::File;
use std::io::{self, BufWriter, Write};
use std::path::{Path, PathBuf};

pub const BIG_BYTES: usize = 40 * 1024 * 1024;
pub const FORMAT_BYTES: usize = 5 * 1024 * 1024;

pub struct Generated {
    pub big: PathBuf,
    pub threadtime: PathBuf,
    pub long: PathBuf,
    pub brief: PathBuf,
    /// 大样本按规则产出的崩溃事件数（每块 Java/Native/ANR 各 1、Tombstone 2）。
    pub big_expected_crashes: usize,
}

struct Lcg(u64);

impl Lcg {
    fn new(seed: u64) -> Self {
        Self(seed)
    }

    fn next(&mut self) -> u64 {
        self.0 = self
            .0
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        self.0
    }
}

/// 在 `dir` 下生成大样本与三种格式样本。
pub fn write_all(dir: &Path) -> io::Result<Generated> {
    Ok(Generated {
        big: dir.join("big_mixed.log"),
        threadtime: dir.join("fmt_threadtime.log"),
        long: dir.join("fmt_long.log"),
        brief: dir.join("fmt_brief.log"),
        big_expected_crashes: 0,
    })
    .and_then(|mut g| {
        g.big_expected_crashes = write_mixed(&g.big, BIG_BYTES)?;
        write_format(&g.threadtime, FORMAT_BYTES, Fmt::Threadtime)?;
        write_format(&g.long, FORMAT_BYTES, Fmt::Long)?;
        write_format(&g.brief, FORMAT_BYTES, Fmt::Brief)?;
        Ok(g)
    })
}

enum Fmt {
    Threadtime,
    Long,
    Brief,
}

fn write_mixed(path: &Path, target: usize) -> io::Result<usize> {
    let mut w = BufWriter::new(File::create(path)?);
    let mut rng = Lcg::new(0x9E37_79B9_7F4A_7C15);
    let mut written = 0usize;
    let mut line_no = 0u64;
    let mut blocks = 0usize;
    let mut expected = 0usize;
    while written < target {
        if line_no % 1000 == 0 {
            let kind = blocks % 4;
            let (lines, events) = crash_block(kind, &mut rng, line_no);
            for l in lines {
                written += l.len() + 1;
                writeln!(w, "{l}")?;
            }
            blocks += 1;
            expected += events;
        } else {
            let l = normal_line(&mut rng, line_no);
            written += l.len() + 1;
            writeln!(w, "{l}")?;
        }
        line_no += 1;
    }
    w.flush()?;
    Ok(expected)
}

fn normal_line(rng: &mut Lcg, n: u64) -> String {
    let pid = rng.next() % 30_000;
    let tid = rng.next() % 30_000;
    format!(
        "11-14 12:00:{:02}.{:03}  {pid}  {tid} I Tag: line {n}",
        (n / 50) % 60,
        n % 1000
    )
}

/// 返回（4 行块, 该块预期崩溃事件数）。
fn crash_block(kind: usize, rng: &mut Lcg, n: u64) -> ([String; 4], usize) {
    let pid = rng.next() % 30_000;
    let tid = rng.next() % 30_000;
    let sec = (n / 50) % 60;
    let ms = n % 1000;
    let head = format!(
        "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} E AndroidRuntime: FATAL EXCEPTION: main"
    );
    let pad1 = format!("11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} I App: pad {n}");
    let pad2 = format!("11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} I App: pad2 {n}");
    let pad3 = format!("11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} I App: pad3 {n}");
    match kind {
        0 => (
            [
                head,
                format!(
                    "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} E AndroidRuntime: Process: com.example.app, PID: {pid}"
                ),
                format!(
                    "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} E AndroidRuntime: java.lang.RuntimeException: boom"
                ),
                pad1,
            ],
            1,
        ),
        1 => (
            [
                format!(
                    "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} F libc: signal 11 (SIGSEGV), code 1"
                ),
                pad1,
                pad2,
                pad3,
            ],
            1,
        ),
        2 => (
            [
                format!(
                    "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} E ActivityManager: ANR in com.example.anr (com.example.anr/.Main)"
                ),
                pad1,
                pad2,
                pad3,
            ],
            1,
        ),
        _ => (
            [
                "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***".to_string(),
                format!("Build fingerprint: 'android/test/{n}'"),
                "signal 6 (SIGABRT), code -6".to_string(),
                pad1,
            ],
            2,
        ),
    }
}

fn write_format(path: &Path, target: usize, fmt: Fmt) -> io::Result<()> {
    let mut w = BufWriter::new(File::create(path)?);
    let mut rng = Lcg::new(0xD1B5_4A32_D192_ED03);
    let mut written = 0usize;
    let mut n = 0u64;
    while written < target {
        let pid = rng.next() % 30_000;
        let tid = rng.next() % 30_000;
        let sec = (n / 50) % 60;
        let ms = n % 1000;
        let line = match fmt {
            Fmt::Threadtime => format!(
                "11-14 12:00:{sec:02}.{ms:03}  {pid}  {tid} I Tag: line {n}"
            ),
            Fmt::Long => format!(
                "[ 11-14 12:00:{sec:02}.{ms:03}  {pid}: {tid} I/Tag ] line {n}"
            ),
            Fmt::Brief => format!("I/Tag({pid}): line {n}"),
        };
        written += line.len() + 1;
        writeln!(w, "{line}")?;
        n += 1;
    }
    w.flush()
}
