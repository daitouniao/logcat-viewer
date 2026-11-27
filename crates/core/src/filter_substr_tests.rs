//! 子串档规模上限用例：条数 / 长度拒绝、归因、分档隔离与不回归。
//!
//! 与 `filter_regex_tests.rs` 同构挂在 `filter::tests` 下，复用父模块的
//! `expr` / `fixture` / `lines_data` / `rows` 等私有辅助。

use super::*;
use crate::error::{CoreError, REGEX_INVALID, SUBSTR_INVALID};

fn compiled_sub(json: &str) -> CompiledMsg {
    FilterExpr::from_json(json)
        .unwrap()
        .compile()
        .unwrap()
        .msg
        .unwrap()
}

fn reject(json: &str) -> CoreError {
    let err = FilterExpr::from_json(json)
        .unwrap()
        .compile()
        .unwrap_err();
    assert_eq!(err.code, SUBSTR_INVALID, "{json}");
    err
}

fn reason(err: &CoreError) -> &str {
    err.params.get("reason").and_then(Value::as_str).unwrap()
}

fn index(err: &CoreError) -> Option<usize> {
    err.params
        .get("index")
        .and_then(Value::as_u64)
        .map(|v| v as usize)
}

fn values_of(items: &[&str]) -> String {
    items
        .iter()
        .map(|v| format!("\"{}\"", v.replace('\\', "\\\\")))
        .collect::<Vec<_>>()
        .join(",")
}

fn substr_condition(items: &[&str], tail: &str) -> String {
    format!(r#"{{"msg":{{"values":[{}]{}}}}}"#, values_of(items), tail)
}

fn scan_rows(json: &str, name: &str, data: &[u8]) -> Vec<u32> {
    let (tf, payload) = fixture(name, data);
    let filter = FilterExpr::from_json(json).unwrap().compile().unwrap();
    let set = scan(&payload, tf.file(), name, &filter).unwrap();
    rows(&set)
}

#[test]
fn substr_limit_boundary_accepted() {
    let items: Vec<String> = (0..16).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let msg = compiled_sub(&substr_condition(&refs, ""));
    assert_eq!(msg.needles.len(), 16);

    let one = "k".repeat(2000);
    let msg = compiled_sub(&substr_condition(&[&one], ""));
    assert_eq!(msg.needles[0].len(), 2000);
}

#[test]
fn substr_limit_count_exceeded_rejects() {
    let items: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let err = reject(&substr_condition(&refs, ""));
    assert_eq!(reason(&err), "too_many");
    assert_eq!(index(&err), None);
}

#[test]
fn substr_limit_length_exceeded_rejects() {
    let long = "k".repeat(2001);
    let err = reject(&substr_condition(&[&long], ""));
    assert_eq!(reason(&err), "too_long");
    assert_eq!(index(&err), Some(0));
}

#[test]
fn substr_limit_index_points_to_offending_entry() {
    let long = "k".repeat(2001);
    let err = reject(&substr_condition(&["ok", &long], ""));
    assert_eq!(reason(&err), "too_long");
    assert_eq!(index(&err), Some(1), "第二条超长 → 下标 1（0 起）");
}

#[test]
fn substr_limit_count_omits_index() {
    let items: Vec<String> = (0..20).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let err = reject(&substr_condition(&refs, ""));
    assert_eq!(reason(&err), "too_many");
    assert!(err.params.get("index").is_none(), "整组拒绝不附 index");
    assert!(
        err.params.get("field").is_none(),
        "规模拒绝不走 INVALID_ARG 的 field"
    );
}

#[test]
fn substr_limit_rejects_even_with_exclude() {
    let items: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let err = reject(&substr_condition(&refs, r#","exclude":true"#));
    assert_eq!(reason(&err), "too_many", "排除模式不豁免规模上限");
}

#[test]
fn substr_limit_does_not_change_match() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta",
        "11-14 12:00:02.000  100  200 I T: alpha beta",
        "11-14 12:00:03.000  100  200 I T: none",
    ]);
    assert_eq!(
        scan_rows(&substr_condition(&["alpha", "beta"], r#","op":"and""#), "sub-and", &data),
        vec![2]
    );
    assert_eq!(
        scan_rows(&substr_condition(&["alpha", "beta"], r#","op":"or""#), "sub-or", &data),
        vec![0, 1, 2]
    );
    let msg = compiled_sub(&substr_condition(&[""], r#","op":"or""#));
    assert!(msg.always_true, "合规规模内空词恒真语义不变");
    assert_eq!(
        scan_rows(&substr_condition(&["alpha"], r#","exclude":true"#), "sub-exc", &data),
        vec![1, 3]
    );
}

#[test]
fn substr_default_mode_unchanged() {
    // 缺省 mode 即子串档，受本卡规模闸约束。
    let items: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    assert_eq!(reject(&substr_condition(&refs, "")).code, SUBSTR_INVALID);

    let msg = compiled_sub(r#"{"msg":{"values":["a.b"]}}"#);
    assert!(msg.routes.is_empty());
    assert!(msg.set.is_none());
    assert_eq!(msg.needles, vec![b"a.b".to_vec()]);
}

#[test]
fn regex_over_limit_not_substr_invalid() {
    // 正则模式的规模超限仍归 REGEX_INVALID(17)，不串到子串档 18。
    let items: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let json = format!(r#"{{"msg":{{"mode":"regex","values":[{}]}}}}"#, values_of(&refs));
    let err = FilterExpr::from_json(&json)
        .unwrap()
        .compile()
        .unwrap_err();
    assert_eq!(err.code, REGEX_INVALID);

    let long = "k".repeat(2001);
    let json = format!(r#"{{"msg":{{"mode":"regex","values":["{}"]}}}}"#, long);
    let err = FilterExpr::from_json(&json)
        .unwrap()
        .compile()
        .unwrap_err();
    assert_eq!(err.code, REGEX_INVALID);
}

#[test]
fn substr_limit_does_not_touch_tags() {
    // 子串规模闸只作用 message，tags 无新增上限：多条非空 tag 正常编译。
    let tags: Vec<String> = (0..20).map(|i| format!("t{i}")).collect();
    let refs: Vec<&str> = tags.iter().map(String::as_str).collect();
    let json = format!(r#"{{"tags":{{"values":[{}]}}}}"#, values_of(&refs));
    let _ = FilterExpr::from_json(&json).unwrap().compile().unwrap();
}
