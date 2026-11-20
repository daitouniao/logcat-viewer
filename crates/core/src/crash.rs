//! 崩溃打标：字节快筛 + 崩溃头前瞻提取（判据与 C# `CrashExtractor` 对齐）。
//!
//! 判据（重误报防护）：
//! - Java：含 `FATAL EXCEPTION:`，且其后 3 行内存在 `Process: <pkg>, PID:` 行；
//!   异常类型取 Process 行之后第一个非空行的首 token，须以已知命名空间开头。
//! - Native / Tombstone：`signal N (SIGXXX)`，SIG 名须在白名单内；窗口内先见
//!   `*** *** ***` 头则归 Tombstone。
//! - ANR：行首 `ANR in ` 且其后首 token 是包名形态。

pub const KIND_JAVA: u8 = 0;
pub const KIND_NATIVE: u8 = 1;
pub const KIND_ANR: u8 = 2;
pub const KIND_TOMBSTONE: u8 = 3;

/// 索引扫描的字节级快筛关键字：任一命中才值得解码文本做完整提取。
const FAST_KEYWORDS: [&[u8]; 4] = [b"FATAL EXCEPTION", b"SIG", b"ANR in ", b"*** ***"];

const SIGNALS: [&str; 8] = [
    "SIGSEGV", "SIGABRT", "SIGBUS", "SIGILL", "SIGFPE", "SIGTRAP", "SIGSYS", "SIGSTKFLT",
];

const EXC_PREFIXES: [&str; 5] = ["java.", "javax.", "android.", "androidx.", "kotlin."];

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CrashMatch {
    pub kind: u8,
    pub exc: Option<String>,
    pub pkg: Option<String>,
}

pub fn contains_keyword(line: &[u8]) -> bool {
    FAST_KEYWORDS
        .iter()
        .any(|kw| line.windows(kw.len()).any(|w| w == *kw))
}

/// 对「崩溃头 msg + 后续若干行 msg」逐行判定；不是崩溃返回 None。
pub fn try_extract(lines: &[&str]) -> Option<CrashMatch> {
    try_java(lines)
        .or_else(|| try_native(lines))
        .or_else(|| try_anr(lines))
}

fn trim_ws(s: &str) -> &str {
    s.trim_start_matches(['\r', '\t', ' '])
}

fn first_token(s: &str) -> &str {
    match s.find([' ', '\t', '\r']) {
        Some(i) => &s[..i],
        None => s,
    }
}

fn try_java(lines: &[&str]) -> Option<CrashMatch> {
    let head = lines.iter().position(|l| l.contains("FATAL EXCEPTION:"))?;
    for j in head + 1..=head + 3 {
        let Some(raw) = lines.get(j) else { break };
        let t = trim_ws(raw);
        if !t.starts_with("Process: ") || !t.contains(", PID") {
            continue;
        }
        return Some(CrashMatch {
            kind: KIND_JAVA,
            exc: find_exc(lines, j),
            pkg: extract_pkg(t),
        });
    }
    None
}

/// `Process: com.example.app, PID: 869` → 包名（非包名形态返回 None）。
fn extract_pkg(t: &str) -> Option<String> {
    let start = t.find("Process: ")? + "Process: ".len();
    let rest = &t[start..];
    let pkg = match rest.find(',') {
        Some(i) => &rest[..i],
        None => rest,
    }
    .trim();
    is_package_name(pkg).then(|| pkg.to_string())
}

/// Process 行之后第一个非空行的首 token；已知命名空间才返回（截到第一个 ':'）。
fn find_exc(lines: &[&str], from: usize) -> Option<String> {
    for k in from + 1..=from + 4 {
        let Some(raw) = lines.get(k) else { break };
        let t = trim_ws(raw);
        if t.is_empty() {
            continue;
        }
        let token = first_token(t);
        for p in EXC_PREFIXES {
            if token.starts_with(p) {
                let exc = match token.find(':') {
                    Some(i) => &token[..i],
                    None => token,
                };
                return Some(exc.to_string());
            }
        }
        return None;
    }
    None
}

fn try_native(lines: &[&str]) -> Option<CrashMatch> {
    let mut tombstone = false;
    for raw in lines {
        let t = trim_ws(raw);
        if !tombstone && t.starts_with("*** *** ***") {
            tombstone = true;
        }
        if let Some(sig) = find_signal(t) {
            return Some(CrashMatch {
                kind: if tombstone {
                    KIND_TOMBSTONE
                } else {
                    KIND_NATIVE
                },
                exc: Some(sig),
                pkg: None,
            });
        }
    }
    None
}

/// 单行内找 `signal N (SIGXXX)`，SIG 名须在白名单内。
fn find_signal(line: &str) -> Option<String> {
    let bytes = line.as_bytes();
    let mut pos = 0;
    while let Some(found) = line[pos..].find("signal") {
        let at = pos + found;
        let mut p = at + 6;
        while p < bytes.len() && (bytes[p] == b' ' || bytes[p] == b'\t') {
            p += 1;
        }
        let digits_start = p;
        while p < bytes.len() && bytes[p].is_ascii_digit() {
            p += 1;
        }
        if p == digits_start {
            pos = at + 6;
            continue;
        }
        while p < bytes.len() && (bytes[p] == b' ' || bytes[p] == b'\t') {
            p += 1;
        }
        if p >= bytes.len() || bytes[p] != b'(' {
            pos = at + 6;
            continue;
        }
        p += 1;
        let Some(close) = line[p..].find(')') else {
            pos = at + 6;
            continue;
        };
        let name = &line[p..p + close];
        if SIGNALS.contains(&name) {
            return Some(name.to_string());
        }
        pos = at + 6;
    }
    None
}

fn try_anr(lines: &[&str]) -> Option<CrashMatch> {
    for raw in lines {
        let t = trim_ws(raw);
        let Some(rest) = t.strip_prefix("ANR in ") else {
            continue;
        };
        let token = first_token(rest).trim();
        if is_package_name(token) {
            return Some(CrashMatch {
                kind: KIND_ANR,
                exc: None,
                pkg: Some(token.to_string()),
            });
        }
    }
    None
}

/// 包名形态：字母开头、点分多段、每段字母数字下划线。
fn is_package_name(s: &str) -> bool {
    if s.chars().count() < 3 {
        return false;
    }
    let mut chars = s.chars();
    let first = chars.next().unwrap();
    if !first.is_ascii_lowercase() {
        return false;
    }
    let mut has_dot = false;
    let mut seg_alpha = true;
    for c in chars {
        if c == '.' {
            if !seg_alpha {
                return false;
            }
            has_dot = true;
            seg_alpha = false;
        } else if c.is_alphanumeric() || c == '_' {
            seg_alpha = true;
        } else {
            return false;
        }
    }
    has_dot && seg_alpha
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crash_extractor_java() {
        let lines = [
            "FATAL EXCEPTION: main",
            "Process: com.example.app, PID: 869",
            "java.lang.IllegalStateException: boom",
        ];
        let m = try_extract(&lines).expect("java crash");
        assert_eq!(m.kind, KIND_JAVA);
        assert_eq!(m.exc.as_deref(), Some("java.lang.IllegalStateException"));
        assert_eq!(m.pkg.as_deref(), Some("com.example.app"));

        // 反例：没有 Process 行不误报
        assert_eq!(try_extract(&["D/App: FATAL EXCEPTION: business log"]), None);
        // 反例：Process 行超出 3 行前瞻
        let far = [
            "FATAL EXCEPTION: main",
            "a",
            "b",
            "c",
            "Process: com.example.app, PID: 1",
        ];
        assert_eq!(try_extract(&far), None);
    }

    #[test]
    fn crash_extractor_native() {
        let m = try_extract(&["signal 11 (SIGSEGV), code 1"]).expect("native");
        assert_eq!(m.kind, KIND_NATIVE);
        assert_eq!(m.exc.as_deref(), Some("SIGSEGV"));
        assert_eq!(m.pkg, None);

        // 反例：白名单外信号名
        assert_eq!(try_extract(&["signal 11 (SIGFOO)"]), None);
        // 反例：普通文本里的 signal 无信号形态
        assert_eq!(try_extract(&["please signal me later"]), None);
    }

    #[test]
    fn crash_extractor_tombstone() {
        let lines = [
            "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***",
            "signal 6 (SIGABRT), code -6",
        ];
        let m = try_extract(&lines).expect("tombstone");
        assert_eq!(m.kind, KIND_TOMBSTONE);
        assert_eq!(m.exc.as_deref(), Some("SIGABRT"));
        assert_eq!(m.pkg, None);
    }

    #[test]
    fn crash_extractor_anr() {
        let m = try_extract(&["ANR in com.example.app (com.example.app/.Main)"]).expect("anr");
        assert_eq!(m.kind, KIND_ANR);
        assert_eq!(m.pkg.as_deref(), Some("com.example.app"));
        assert_eq!(m.exc, None);

        // 反例：ANR 缩写后面不是包名形态
        assert_eq!(try_extract(&["ANR in this build"]), None);
        // 反例：不在行首
        assert_eq!(try_extract(&["before ANR in com.example.app"]), None);
    }
}
