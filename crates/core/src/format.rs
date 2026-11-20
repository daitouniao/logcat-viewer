//! 不支持的格式判定（spec「不支持的格式拒绝」）：魔数先于文本嗅探。
//!
//! 只认已知魔数并给出格式标签；文本判据仍归 `probe`。后续 DLT 解码在同一位置
//! 长出「识别后走解码」的路由。

/// DLT 文件魔数。
const DLT_MAGIC: [u8; 4] = [0x44, 0x4C, 0x54, 0x01];

/// 命中已知魔数返回格式标签，其余返回 `None` 交给文本嗅探。
pub fn detect_unsupported_format(sample: &[u8]) -> Option<&'static str> {
    if sample.len() >= DLT_MAGIC.len() && sample[..DLT_MAGIC.len()] == DLT_MAGIC {
        return Some("Dlt");
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_detects_dlt_magic() {
        let mut bytes = DLT_MAGIC.to_vec();
        bytes.extend([0u8; 64]);
        assert_eq!(detect_unsupported_format(&bytes), Some("Dlt"));
    }

    #[test]
    fn format_requires_four_magic_bytes() {
        assert_eq!(detect_unsupported_format(&[]), None);
        assert_eq!(detect_unsupported_format(&[0x44, 0x4C, 0x54]), None);
        assert_eq!(detect_unsupported_format(&[0x44, 0x4C, 0x54, 0x02]), None);
    }

    #[test]
    fn format_plain_text_is_none() {
        assert_eq!(
            detect_unsupported_format(b"11-14 12:00:01.123  1  2 I Tag: hello\n"),
            None
        );
    }
}
