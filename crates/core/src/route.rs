//! 代理路由三态判定（spec「代理路由三态判定」）。

use crate::request::Mode;

/// 九格真值表：开启恒走代理；自动在直接打开失败（含读窗口 I/O 失败）时走代理
/// ——嗅探判非文本的输入在进入路由前已被拒绝；关闭恒不走。
pub fn should_use_proxy(mode: Mode, direct_open_succeeded: bool, looks_like_text: bool) -> bool {
    match mode {
        Mode::On => true,
        Mode::Auto => !direct_open_succeeded || !looks_like_text,
        Mode::Off => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::request::Mode;

    #[test]
    fn proxy_decision_nine_cell_truth_table() {
        let cases: [(Mode, bool, bool, bool); 12] = [
            (Mode::Off, true, true, false),
            (Mode::Off, true, false, false),
            (Mode::Off, false, true, false),
            (Mode::Off, false, false, false),
            (Mode::On, true, true, true),
            (Mode::On, true, false, true),
            (Mode::On, false, true, true),
            (Mode::On, false, false, true),
            (Mode::Auto, true, true, false),
            (Mode::Auto, true, false, true),
            (Mode::Auto, false, true, true),
            (Mode::Auto, false, false, true),
        ];
        for (mode, opened, text, expected) in cases {
            assert_eq!(
                should_use_proxy(mode, opened, text),
                expected,
                "mode={mode:?} open={opened} text={text}"
            );
        }
    }

    #[test]
    fn unknown_mode_falls_back_to_off() {
        let req = crate::request::parse(
            &serde_json::json!({
                "kind": "File",
                "path": "C:\\a.log",
                "mode": "Nonexistent",
                "proxy": { "pipeName": "p", "launchFile": "C:\\p.exe", "launchTimeoutMs": 1000 },
                "options": { "tempDir": "C:\\t", "sidecar": { "dir": "C:\\c" } }
            })
            .to_string(),
        )
        .unwrap();
        assert_eq!(req.mode, Mode::Off);
        for opened in [true, false] {
            for text in [true, false] {
                assert!(!should_use_proxy(req.mode, opened, text));
            }
        }
    }
}