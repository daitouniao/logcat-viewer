//! 文本嗅探（D5 / spec「文本嗅探规则」）：固定窗口读头部，统计控制字符占比，
//! 严格大于阈值判非文本；空文件判文本；宽字符（UTF-16）特征判非文本。

use std::fs::File;
use std::io::Read;

/// 白名单之外的控制字符与删除符计入「非文本」证据。
pub fn is_control_byte(b: u8) -> bool {
    (b < 0x20 && !matches!(b, 0x09 | 0x0A | 0x0C | 0x0D)) || b == 0x7F
}

/// 字节序特征：BOM，或明显的宽字符零字节排布。
pub fn looks_like_wide_encoding(sample: &[u8]) -> bool {
    if sample.starts_with(&[0xFF, 0xFE]) || sample.starts_with(&[0xFE, 0xFF]) {
        return true;
    }
    // UTF-16LE ASCII 文本每两字节一个 0x00；UTF-16BE 每两字节首个为 0x00
    if sample.len() >= 4 {
        let le_zeros = sample.iter().skip(1).step_by(2).filter(|b| **b == 0).count();
        let be_zeros = sample.iter().step_by(2).filter(|b| **b == 0).count();
        let pairs = sample.len() / 2;
        if pairs >= 2 && ((le_zeros * 2 >= pairs) || (be_zeros * 2 >= pairs)) {
            return true;
        }
    }
    false
}

/// 判定一段样本是否为文本。空样本（空文件）判文本。
pub fn sample_is_text(sample: &[u8], threshold: f64) -> bool {
    if sample.is_empty() {
        return true;
    }
    if looks_like_wide_encoding(sample) {
        return false;
    }
    let control = sample.iter().filter(|b| is_control_byte(**b)).count();
    let ratio = control as f64 / sample.len() as f64;
    // 严格大于阈值才判非文本
    ratio <= threshold
}

/// 从已打开的文件读窗口并判定。
pub fn probe_open_file(file: &mut File, window: usize, threshold: f64) -> std::io::Result<bool> {
    let mut buf = vec![0u8; window.max(1)];
    let mut filled = 0usize;
    while filled < buf.len() {
        match file.read(&mut buf[filled..])? {
            0 => break,
            n => filled += n,
        }
    }
    buf.truncate(filled);
    Ok(sample_is_text(&buf, threshold))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn temp_file(name: &str, bytes: &[u8]) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "logcore-probe-{}-{}",
            std::process::id(),
            name
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("sample.log");
        std::fs::write(&path, bytes).unwrap();
        path
    }

    #[test]
    fn probe_window_boundary_and_truncation() {
        // 前 8 KB 是文本，之后全是控制字符：只看窗口
        let mut bytes = vec![b'a'; 8192];
        bytes.extend(std::iter::repeat(0x01).take(4096));
        assert!(sample_is_text(&bytes[..8192], 0.10));

        let path = temp_file("window", &bytes);
        let mut f = std::fs::File::open(&path).unwrap();
        assert!(probe_open_file(&mut f, 8192, 0.10).unwrap());
        let mut f = std::fs::File::open(&path).unwrap();
        assert!(!probe_open_file(&mut f, 16384, 0.10).unwrap());

        // 小于窗口的文件按实际长度判定
        let path = temp_file("small", b"hello\n");
        let mut f = std::fs::File::open(&path).unwrap();
        assert!(probe_open_file(&mut f, 8192, 0.10).unwrap());
    }

    #[test]
    fn probe_control_char_whitelist() {
        // 09 / 0A / 0C / 0D 不计入；7F 计入
        let whitelisted = b"a\tb\nc\x0cd\r\n";
        assert!(sample_is_text(whitelisted, 0.0));
        for b in [0x09u8, 0x0A, 0x0C, 0x0D] {
            assert!(!is_control_byte(b));
        }
        assert!(is_control_byte(0x7F));
        assert!(is_control_byte(0x00));
        assert!(!is_control_byte(b'a'));
        assert!(!sample_is_text(&[0x7F; 100], 0.10));
    }

    #[test]
    fn probe_strictly_greater_than_threshold() {
        // 占比恰为 10%：判文本
        let mut bytes = vec![b'a'; 90];
        bytes.extend(std::iter::repeat(0x01).take(10));
        assert_eq!(bytes.len(), 100);
        assert!(sample_is_text(&bytes, 0.10));
        // 略高于 10%：判非文本
        let mut bytes = vec![b'a'; 89];
        bytes.extend(std::iter::repeat(0x01).take(11));
        assert!(!sample_is_text(&bytes, 0.10));
    }

    #[test]
    fn probe_empty_file_is_text() {
        assert!(sample_is_text(&[], 0.10));
        let path = temp_file("empty", b"");
        let mut f = std::fs::File::open(&path).unwrap();
        assert!(probe_open_file(&mut f, 8192, 0.10).unwrap());
    }

    #[test]
    fn probe_utf16_is_binary() {
        // BOM
        let mut bom = vec![0xFF, 0xFE];
        bom.extend("hello".encode_utf16().flat_map(|u| u.to_le_bytes()));
        assert!(!sample_is_text(&bom, 0.10));
        // 无 BOM 但零字节排布明显
        let le: Vec<u8> = "hello world".encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
        assert!(!sample_is_text(&le, 0.10));
        // 普通 UTF-8 中文不受影响
        assert!(sample_is_text("中文日志\n".as_bytes(), 0.10));
    }

    #[test]
    fn probe_reads_whole_file_via_writer() {
        let path = temp_file("write", b"");
        let mut f = std::fs::OpenOptions::new().append(true).open(&path).unwrap();
        f.write_all(b"line1\nline2\n").unwrap();
        let mut r = std::fs::File::open(&path).unwrap();
        assert!(probe_open_file(&mut r, 8192, 0.10).unwrap());
    }
}