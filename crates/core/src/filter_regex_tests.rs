//! 正则档用例：校验归因、上限、字面量路由与扫描语义。

use super::*;
use crate::error::{CoreError, REGEX_INVALID};

fn compiled(json: &str) -> CompiledMsg {
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
    assert_eq!(err.code, REGEX_INVALID, "{json}");
    err
}

fn reason(err: &CoreError) -> &str {
    err.params.get("reason").and_then(Value::as_str).unwrap()
}

fn index(err: &CoreError) -> Option<usize> {
    err.params.get("index").and_then(Value::as_u64).map(|v| v as usize)
}

fn values_of(items: &[&str]) -> String {
    items
        .iter()
        .map(|v| format!("\"{}\"", v.replace('\\', "\\\\")))
        .collect::<Vec<_>>()
        .join(",")
}

fn regex_condition(items: &[&str], tail: &str) -> String {
    format!(r#"{{"msg":{{"mode":"regex","values":[{}]{}}}}}"#, values_of(items), tail)
}

fn scan_rows(json: &str, name: &str, data: &[u8]) -> Vec<u32> {
    let (tf, payload) = fixture(name, data);
    let filter = FilterExpr::from_json(json).unwrap().compile().unwrap();
    let set = scan(&payload, tf.file(), name, &filter).unwrap();
    rows(&set)
}

#[test]
fn filter_expr_accepts_regex_mode() {
    let parsed = expr(r#"{"msg":{"values":["a+b"],"mode":"regex"}}"#);
    assert_eq!(
        parsed.msg.as_ref().unwrap().mode.as_deref(),
        Some("regex")
    );
    let msg = compiled(r#"{"msg":{"values":["a+b"],"mode":"regex"}}"#);
    assert_eq!(msg.routes, vec![Route::Set(0)]);
    assert!(msg.set.is_some());
    assert!(msg.needles.is_empty());
    assert!(!msg.always_true);
}

#[test]
fn filter_expr_regex_mode_defaults() {
    let msg = compiled(r#"{"msg":{"values":["x"],"mode":"regex"}}"#);
    assert_eq!(msg.op, BoolOp::And);
    assert!(!msg.case);
    assert!(!msg.exclude);
    assert!(!msg.always_true);
    assert!(msg.set.is_some());
    assert!(msg.needles.is_empty());

    let msg = compiled(r#"{"msg":{"values":["x","y"],"mode":"regex","case":true}}"#);
    assert!(msg.set.is_none());
    assert_eq!(msg.routes, vec![Route::Literal(0), Route::Literal(1)]);
    assert_eq!(msg.needles, vec![b"x".to_vec(), b"y".to_vec()]);
}

#[test]
fn regex_reject_reason_syntax() {
    let err = reject(&regex_condition(&["(unclosed"], ""));
    assert_eq!(reason(&err), "syntax");
    assert_eq!(index(&err), Some(0));

    let err = reject(&regex_condition(&["a(?=b)"], ""));
    assert_eq!(reason(&err), "syntax");
}

#[test]
fn regex_reject_reason_too_long() {
    let long = "k".repeat(2001);
    let err = reject(&regex_condition(&[&long], ""));
    assert_eq!(reason(&err), "too_long");
    assert_eq!(index(&err), Some(0));
}

#[test]
fn regex_reject_reason_too_many() {
    let items: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let refs: Vec<&str> = items.iter().map(String::as_str).collect();
    let err = reject(&regex_condition(&refs, ""));
    assert_eq!(reason(&err), "too_many");
    assert_eq!(index(&err), None);
}

#[test]
fn regex_reject_reason_too_size() {
    let err = reject(&regex_condition(&[r"(\w+\s?){200}"], ""));
    assert_eq!(reason(&err), "too_size");
    assert_eq!(index(&err), Some(0));
}

#[test]
fn regex_reject_attributed_index_points_to_offending_entry() {
    let err = reject(&regex_condition(&["ok1", "ok2", "(bad", r"(\w+\s?){200}"], ""));
    assert_eq!(reason(&err), "syntax");
    assert_eq!(index(&err), Some(2));

    let err = reject(&regex_condition(&["ok1", "ok2", r"(\w+\s?){200}"], ""));
    assert_eq!(reason(&err), "too_size");
    assert_eq!(index(&err), Some(2));

    let long = "k".repeat(2001);
    let err = reject(&regex_condition(&["ok1", &long, "(bad"], ""));
    assert_eq!(reason(&err), "too_long");
    assert_eq!(index(&err), Some(1));
}

#[test]
fn regex_reject_group_size_omits_index() {
    let items: Vec<&str> = vec![r"(\w+\s?){12}"; 16];
    let err = reject(&regex_condition(&items, ""));
    assert_eq!(reason(&err), "too_size");
    assert_eq!(index(&err), None);
}

#[test]
fn regex_reject_bounded_pattern_no_arg_error() {
    let long = "k".repeat(2001);
    let many: Vec<String> = (0..17).map(|i| format!("p{i}")).collect();
    let many_refs: Vec<&str> = many.iter().map(String::as_str).collect();
    let cases = [
        regex_condition(&["(unclosed"], ""),
        regex_condition(&[&long], ""),
        regex_condition(&many_refs, ""),
        regex_condition(&[r"(\w+\s?){200}"], ""),
    ];
    for json in &cases {
        let err = reject(json);
        assert!(err.params.get("field").is_none(), "{json}");
        assert!(
            matches!(
                reason(&err),
                "syntax" | "too_long" | "too_many" | "too_size"
            ),
            "{json}"
        );
    }
}

#[test]
fn regex_accepts_common_forms() {
    let literal2000 = "l".repeat(2000);
    let nested = format!("{}x{}", "(".repeat(10), ")".repeat(10));
    let cases = [
        "(a|b|c|d|e|f|g|h|i|j|k|l|m|n|o|p|q|r|s|t)".to_string(),
        literal2000,
        r"t\s?i\s?m\s?e\s?o\s?u\s?t".to_string(),
        nested,
        "(x|x)+y".to_string(),
        r"[A-Za-z_][A-Za-z0-9_]*".to_string(),
        r"(a|b|c){50}".to_string(),
        r"\p{Greek}+\w*".to_string(),
    ];
    for case in &cases {
        for tail in ["", r#","case":true"#] {
            let json = regex_condition(&[case], tail);
            let msg = compiled(&json);
            assert!(!msg.always_true, "{json}");
        }
    }
    let refs: Vec<&str> = cases.iter().map(String::as_str).collect();
    compiled(&regex_condition(&refs, ""));
    compiled(&regex_condition(&refs, r#","case":true"#));
}

#[test]
fn regex_pure_literal_short_circuit_hits() {
    let msg = compiled(r#"{"msg":{"values":["tim\\.eout"],"mode":"regex","case":true}}"#);
    assert_eq!(msg.routes, vec![Route::Literal(0)]);
    assert!(msg.set.is_none());
    let data = lines_data(&[
        "11-14 12:00:00.000  1  2 I T: tim.eout here",
        "11-14 12:00:01.000  1  2 I T: timxeout here",
    ]);
    assert_eq!(
        scan_rows(r#"{"msg":{"values":["tim\\.eout"],"mode":"regex","case":true}}"#, "lit-hit", &data),
        vec![0]
    );
}

#[test]
fn regex_escaped_dot_equals_substring_result() {
    let data = lines_data(&[
        "11-14 12:00:00.000  1  2 I T: tim.eout here",
        "11-14 12:00:01.000  1  2 I T: timxeout here",
        "11-14 12:00:02.000  1  2 I T: nothing",
    ]);
    let substring = scan_rows(r#"{"msg":{"values":["tim.eout"],"case":true}}"#, "dot-sub", &data);
    let regex = scan_rows(r#"{"msg":{"values":["tim\\.eout"],"mode":"regex","case":true}}"#, "dot-re", &data);
    assert_eq!(substring, vec![0]);
    assert_eq!(regex, substring);
}

#[test]
fn regex_anchor_not_short_circuited() {
    let msg = compiled(r#"{"msg":{"values":["^abc$"],"mode":"regex","case":true}}"#);
    assert_eq!(msg.routes, vec![Route::Set(0)]);
    assert!(msg.set.is_some());
    let data = lines_data(&[
        "11-14 12:00:00.000  1  2 I T: abc",
        "11-14 12:00:01.000  1  2 I T: xabcx",
    ]);
    assert_eq!(
        scan_rows(r#"{"msg":{"values":["abc"],"mode":"regex","case":true}}"#, "anchor-re", &data),
        vec![0, 1]
    );
}

#[test]
fn regex_mixed_batch_routes_per_entry() {
    let msg = compiled(r#"{"msg":{"values":["lit","a+b"],"mode":"regex","case":true}}"#);
    assert_eq!(msg.routes, vec![Route::Literal(0), Route::Set(0)]);
    assert_eq!(msg.needles, vec![b"lit".to_vec()]);
    assert!(msg.set.is_some());
    let data = lines_data(&[
        "11-14 12:00:00.000  1  2 I T: lit",
        "11-14 12:00:01.000  1  2 I T: aaaab",
        "11-14 12:00:02.000  1  2 I T: neither",
    ]);
    assert_eq!(
        scan_rows(
            r#"{"msg":{"values":["lit","a+b"],"mode":"regex","case":true,"op":"or"}}"#,
            "mixed-or",
            &data
        ),
        vec![0, 1]
    );
    assert_eq!(
        scan_rows(
            r#"{"msg":{"values":["lit","a+b"],"mode":"regex","case":true,"op":"and"}}"#,
            "mixed-and",
            &data
        ),
        Vec::<u32>::new()
    );
}

#[test]
fn regex_all_literal_batch_equals_substring_mode() {
    let data = lines_data(&[
        "11-14 12:00:00.000  1  2 I T: alpha beta",
        "11-14 12:00:01.000  1  2 I T: alpha",
        "11-14 12:00:02.000  1  2 I T: beta",
        "11-14 12:00:03.000  1  2 I T: none",
    ]);
    for op in ["and", "or"] {
        let substring = scan_rows(
            &format!(r#"{{"msg":{{"values":["alpha","beta"],"op":"{op}","case":true}}}}"#),
            "lit-sub",
            &data,
        );
        let regex = scan_rows(
            &format!(
                r#"{{"msg":{{"mode":"regex","values":["alpha","beta"],"op":"{op}","case":true}}}}"#
            ),
            "lit-re",
            &data,
        );
        assert_eq!(regex, substring, "op={op}");
    }
}

fn dirty_data() -> Vec<u8> {
    let mut data = b"11-14 12:00:00.000  100  200 I Tag: A".to_vec();
    data.push(0xFF);
    data.extend_from_slice(b"B\n");
    data.extend_from_slice(b"11-14 12:00:01.000  100  200 I Tag: AB\n");
    data
}

fn empty_body_data() -> Vec<u8> {
    lines_data(&[
        "11-14 12:00:00.000  100  200 I Tag:",
        "11-14 12:00:01.000  100  200 I Tag: x",
    ])
}

#[test]
fn regex_dirty_row_matches_replacement_char() {
    let data = dirty_data();
    let rows = scan_rows(&regex_condition(&["\u{FFFD}"], r#","case":true"#), "re-lossy", &data);
    assert_eq!(rows, vec![0]);
}

#[test]
fn regex_dirty_row_spanned_by_wildcard() {
    let data = dirty_data();
    let rows = scan_rows(&regex_condition(&["A.*B"], r#","case":true"#), "re-lossy-span", &data);
    assert_eq!(rows, vec![0, 1], "非法字节归一后可被通配跨连");
}

#[test]
fn regex_multi_pattern_equals_single_pattern_union() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha only",
        "11-14 12:00:01.000  100  200 I T: beta only",
        "11-14 12:00:02.000  100  200 I T: gamma only",
        "11-14 12:00:03.000  100  200 I T: none of them",
    ]);
    let mut union: Vec<u32> = Vec::new();
    for p in ["alpha", "beta", "gamma"] {
        for r in scan_rows(&regex_condition(&[p], ""), "re-union-one", &data) {
            if !union.contains(&r) {
                union.push(r);
            }
        }
    }
    union.sort_unstable();
    let batch = scan_rows(
        &regex_condition(&["alpha", "beta", "gamma"], r#","op":"or""#),
        "re-union-batch",
        &data,
    );
    assert_eq!(batch, union);
}

#[test]
fn regex_empty_body_anchor_matches() {
    let msg = compiled(r#"{"msg":{"values":["^abc$"],"mode":"regex","case":true}}"#);
    assert!(matches!(msg.routes.as_slice(), [Route::Set(0)]), "锚点不进短路");
    let rows = scan_rows(&regex_condition(&["^$"], ""), "re-empty-anchor", &empty_body_data());
    assert_eq!(rows, vec![0]);
}

#[test]
fn regex_empty_body_star_matches() {
    let rows = scan_rows(&regex_condition(&["a*"], ""), "re-empty-star", &empty_body_data());
    assert_eq!(rows, vec![0, 1], "零宽命中对空正文同样成立");
}

#[test]
fn regex_empty_body_word_boundary_misses() {
    let rows = scan_rows(&regex_condition(&["\\b"], ""), "re-empty-boundary", &empty_body_data());
    assert_eq!(rows, vec![1], "空正文无词边界");
}

#[test]
fn regex_empty_pattern_always_true_skips_read() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta",
    ]);
    let (tf, payload) = fixture("re-empty-always", &data);
    let filter = FilterExpr::from_json(&regex_condition(&[""], r#","op":"or","case":true"#))
        .unwrap()
        .compile()
        .unwrap();
    let msg = filter.msg.as_ref().unwrap();
    assert!(msg.always_true);
    let (result, stats) = scan_with_chunk_stats(&payload, tf.file(), "re-empty-always", &filter, 1);
    assert_eq!(rows(&result.unwrap()), vec![0, 1]);
    assert_eq!(stats.bytes, 0, "恒真条件不回读正文");

    // 缺省忽略大小写时空 pattern 走 set，恒命中结论不变（只是省不掉回读）
    let msg = compiled(&regex_condition(&[""], r#","op":"or""#));
    assert!(!msg.always_true);
    assert!(matches!(msg.routes.as_slice(), [Route::Set(0)]));
    assert_eq!(
        scan_rows(&regex_condition(&[""], r#","op":"or""#), "re-empty-always-ci", &data),
        vec![0, 1]
    );
}

#[test]
fn regex_empty_pattern_exclude_yields_empty() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta",
    ]);
    for tail in [r#","exclude":true"#, r#","exclude":true,"case":true"#] {
        let rows = scan_rows(
            &regex_condition(&[""], tail),
            "re-empty-exclude",
            &data,
        );
        assert!(rows.is_empty(), "{tail}");
    }
}

#[test]
fn regex_case_insensitive_default_hits() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: TIMEOUT",
        "11-14 12:00:01.000  100  200 I T: other",
    ]);
    let msg = compiled(r#"{"msg":{"values":["timeout"],"mode":"regex"}}"#);
    assert!(matches!(msg.routes.as_slice(), [Route::Set(0)]));
    assert_eq!(
        scan_rows(r#"{"msg":{"values":["timeout"],"mode":"regex"}}"#, "re-ci", &data),
        vec![0]
    );
}

#[test]
fn regex_case_sensitive_misses() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: TIMEOUT",
        "11-14 12:00:01.000  100  200 I T: timeout",
    ]);
    assert_eq!(
        scan_rows(
            r#"{"msg":{"values":["timeout"],"mode":"regex","case":true}}"#,
            "re-cs",
            &data
        ),
        vec![1]
    );
}

#[test]
fn regex_inline_ignore_case_overrides_outer_case() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: Timeout",
        "11-14 12:00:01.000  100  200 I T: other",
    ]);
    assert_eq!(
        scan_rows(
            r#"{"msg":{"values":["(?i)timeout"],"mode":"regex","case":true}}"#,
            "re-inline-i",
            &data
        ),
        vec![0]
    );
}

#[test]
fn regex_inline_case_sensitive_overrides_default() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: timeout",
        "11-14 12:00:01.000  100  200 I T: TIMEOUT",
        "11-14 12:00:02.000  100  200 I T: Timeout",
    ]);
    assert_eq!(
        scan_rows(
            r#"{"msg":{"values":["(?-i)timeout"],"mode":"regex"}}"#,
            "re-inline-ii",
            &data
        ),
        vec![0]
    );
}

#[test]
fn regex_sharp_s_fold_equivalent() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: stra\u{DF}e",
        "11-14 12:00:01.000  100  200 I T: STRA\u{1E9E}E",
    ]);
    assert!(
        scan_rows(&regex_condition(&["strasse"], ""), "re-ss-split", &data).is_empty(),
        "折叠不展开 \u{DF} 为 ss"
    );
    assert_eq!(
        scan_rows(&regex_condition(&["stra\u{DF}e"], ""), "re-ss-fold", &data),
        vec![0, 1]
    );
}

#[test]
fn regex_or_equals_merged_alternation_oracle() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: gc pause long",
        "11-14 12:00:01.000  100  200 I T: anr in com.x",
        "11-14 12:00:02.000  100  200 I T: quiet",
        "11-14 12:00:03.000  100  200 I T: both gc pause anr",
    ]);
    let batch = scan_rows(
        &regex_condition(&["gc pause", "anr in"], r#","op":"or""#),
        "re-or-batch",
        &data,
    );
    let merged = scan_rows(
        &regex_condition(&["gc pause|anr in"], ""),
        "re-or-merged",
        &data,
    );
    assert_eq!(batch, merged);
    assert_eq!(batch, vec![0, 1, 3]);
}

#[test]
fn regex_or_equals_per_pattern_oracle() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: gc pause long",
        "11-14 12:00:01.000  100  200 I T: anr in com.x",
        "11-14 12:00:02.000  100  200 I T: quiet",
        "11-14 12:00:03.000  100  200 I T: both gc pause anr",
    ]);
    let mut union: Vec<u32> = Vec::new();
    for p in ["gc pause", "anr in", "quiet"] {
        for r in scan_rows(&regex_condition(&[p], ""), "re-each", &data) {
            if !union.contains(&r) {
                union.push(r);
            }
        }
    }
    union.sort_unstable();
    let batch = scan_rows(
        &regex_condition(&["gc pause", "anr in", "quiet"], r#","op":"or""#),
        "re-or-three",
        &data,
    );
    assert_eq!(batch, union);
}

#[test]
fn regex_and_requires_all_patterns() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: GC pause",
        "11-14 12:00:01.000  100  200 I T: GC only",
        "11-14 12:00:02.000  100  200 I T: pause only",
    ]);
    assert_eq!(
        scan_rows(
            &regex_condition(&["GC", "pause"], r#","op":"and""#),
            "re-and",
            &data
        ),
        vec![0]
    );
}

#[test]
fn regex_exclude_keeps_misses() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: hit me",
        "11-14 12:00:01.000  100  200 I T: keep",
        "11-14 12:00:02.000  100  200 I T: also keep",
    ]);
    let hit = scan_rows(&regex_condition(&["hit"], ""), "re-inc", &data);
    let excluded = scan_rows(&regex_condition(&["hit"], r#","exclude":true"#), "re-exc", &data);
    assert_eq!(hit, vec![0]);
    assert_eq!(excluded, vec![1, 2]);
}

#[test]
fn regex_value_order_independent() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta",
        "11-14 12:00:02.000  100  200 I T: alpha beta",
        "11-14 12:00:03.000  100  200 I T: gamma",
    ]);
    for op in ["or", "and"] {
        let forward = scan_rows(
            &regex_condition(&["alpha", "beta", r"a\w*ha"], &format!(r#","op":"{op}""#)),
            "re-order-a",
            &data,
        );
        let reverse = scan_rows(
            &regex_condition(&[r"a\w*ha", "beta", "alpha"], &format!(r#","op":"{op}""#)),
            "re-order-b",
            &data,
        );
        assert_eq!(forward, reverse, "op={op}");
    }
}

#[test]
fn regex_chunk_size_invariant() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: gc pause long body",
        "11-14 12:00:01.000  100  200 I T: quiet",
        "11-14 12:00:02.000  100  200 I T: another gc pause here",
    ]);
    let (tf, payload) = fixture("re-chunk", &data);
    let filter = FilterExpr::from_json(&regex_condition(&["gc\\s+pause", "quiet"], r#","op":"or""#))
        .unwrap()
        .compile()
        .unwrap();
    let default = rows(&scan(&payload, tf.file(), "re-chunk", &filter).unwrap());
    let tiny = rows(&scan_with_chunk(&payload, tf.file(), "re-chunk", &filter, 1).unwrap());
    assert_eq!(default, vec![0, 1, 2]);
    assert_eq!(default, tiny);
}

#[test]
fn regex_repeat_yields_same_result() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta alpha",
        "11-14 12:00:02.000  100  200 I T: gamma",
    ]);
    let (tf, payload) = fixture("re-repeat", &data);
    let filter = FilterExpr::from_json(&regex_condition(&["alpha", r"b\w+"], r#","op":"or""#))
        .unwrap()
        .compile()
        .unwrap();
    let first = rows(&scan(&payload, tf.file(), "re-repeat", &filter).unwrap());
    let second = rows(&scan(&payload, tf.file(), "re-repeat", &filter).unwrap());
    assert_eq!(first, second);
    assert_eq!(first, vec![0, 1]);
}

#[test]
fn regex_does_not_mutate_snapshot() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: alpha",
        "11-14 12:00:01.000  100  200 I T: beta",
    ]);
    let (tf, payload) = fixture("re-snapshot", &data);
    let before = payload.clone();
    let filter = FilterExpr::from_json(&regex_condition(&["alph"], "")).unwrap().compile().unwrap();
    let _ = scan(&payload, tf.file(), "re-snapshot", &filter).unwrap();
    assert_eq!(payload, before);
}

#[test]
fn regex_mode_does_not_change_substring_result() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: line 1",
        "11-14 12:00:01.000  100  200 I T: line 2",
        "11-14 12:00:02.000  100  200 I T: other",
    ]);
    let substring = scan_rows(r#"{"msg":{"values":["line"]}}"#, "re-vs-sub", &data);
    let regex = scan_rows(
        r#"{"msg":{"mode":"regex","values":["line\\."],"case":true}}"#,
        "re-vs-re",
        &data,
    );
    let regex_plain = scan_rows(r#"{"msg":{"mode":"regex","values":["line"]}}"#, "re-vs-re2", &data);
    assert_eq!(substring, vec![0, 1]);
    assert_eq!(regex, Vec::<u32>::new(), "带元字符的转义只命中 `line.`");
    assert_eq!(regex_plain, substring);
}

#[test]
fn substring_default_mode_unchanged() {
    let msg = compiled(r#"{"msg":{"values":["a.b"]}}"#);
    assert!(msg.routes.is_empty());
    assert!(msg.set.is_none());
    assert_eq!(msg.needles, vec![b"a.b".to_vec()]);
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: has a.b here",
        "11-14 12:00:01.000  100  200 I T: has axb here",
    ]);
    assert_eq!(scan_rows(r#"{"msg":{"values":["a.b"]}}"#, "sub-unchanged", &data), vec![0]);
}

#[test]
fn regex_rejects_lookahead() {
    let err = reject(&regex_condition(&["a(?=b)"], ""));
    assert_eq!(reason(&err), "syntax");
    assert_eq!(index(&err), Some(0));
}

#[test]
fn regex_rejects_lookbehind() {
    let err = reject(&regex_condition(&["(?<=a)b"], ""));
    assert_eq!(reason(&err), "syntax");
}

#[test]
fn regex_rejects_backreference() {
    let err = reject(&regex_condition(&[r"(a)\1"], ""));
    assert_eq!(reason(&err), "syntax");
}

#[test]
fn regex_rejects_atomic_group() {
    let err = reject(&regex_condition(&["(?>a)"], ""));
    assert_eq!(reason(&err), "syntax");
}

#[test]
fn regex_stacked_quantifier_is_not_possessive() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: aaa",
        "11-14 12:00:01.000  100  200 I T: xyz",
        "11-14 12:00:02.000  100  200 I T: aab",
    ]);
    let stacked = scan_rows(&regex_condition(&["a*+"], r#","case":true"#), "re-stack", &data);
    assert_eq!(
        stacked,
        scan_rows(&regex_condition(&[r"(a*)+"], r#","case":true"#), "re-nest", &data),
        "量词叠加写法与显式嵌套同解"
    );
    assert_eq!(
        stacked,
        scan_rows(&regex_condition(&["a**"], r#","case":true"#), "re-star", &data),
        "两层量词不受书写顺序影响"
    );
    assert_eq!(stacked, vec![0, 1, 2], "内层可空匹配，故整条正则命中所有行");
}

#[test]
fn regex_rejects_unclosed_group() {
    let err = reject(&regex_condition(&["(abc"], ""));
    assert_eq!(reason(&err), "syntax");
}

#[test]
fn regex_unicode_word_class_on_chinese() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: \u{4e2d}\u{6587}\u{8bcd}",
        "11-14 12:00:01.000  100  200 I T: ascii",
        "11-14 12:00:02.000  100  200 I T: 1234",
    ]);
    assert_eq!(
        scan_rows(&regex_condition(&[r"\w+"], ""), "re-uni-word", &data),
        vec![0, 1, 2]
    );
}

#[test]
fn regex_property_class_uppercase_letter() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: value",
        "11-14 12:00:01.000  100  200 I T: VALUE",
    ]);
    assert_eq!(
        scan_rows(&regex_condition(&[r"\p{Lu}+"], r#","case":true"#), "re-prop", &data),
        vec![1]
    );
}

#[test]
fn regex_class_set_operation() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: acd",
        "11-14 12:00:01.000  100  200 I T: b",
    ]);
    assert_eq!(
        scan_rows(&regex_condition(&[r"[a-z--b]+"], r#","case":true"#), "re-set-op", &data),
        vec![0]
    );
}

#[test]
fn regex_verbose_flag_ignores_pattern_whitespace() {
    let joined = lines_data(&["11-14 12:00:00.000  100  200 I T: ab gap"]);
    let spaced = lines_data(&["11-14 12:00:00.000  100  200 I T: a b gap"]);
    assert_eq!(
        scan_rows(&regex_condition(&[r"(?x)a b"], r#","case":true"#), "re-verb-plain", &joined),
        vec![0],
        "宽松模式下的裸空白不入匹配"
    );
    assert!(
        scan_rows(&regex_condition(&[r"(?x)a b"], r#","case":true"#), "re-verb-spaced", &spaced)
            .is_empty()
    );
    assert_eq!(
        scan_rows(&regex_condition(&[r"(?x)a\ b"], r#","case":true"#), "re-verb-esc", &spaced),
        vec![0],
        "转义空白才作为要匹配的字符"
    );
    assert!(
        scan_rows(&regex_condition(&["a b"], r#","case":true"#), "re-verb-off", &joined).is_empty(),
        "未开宽松模式时裸空白是普通字符"
    );
}

#[test]
fn regex_multiline_anchor_misses_crlf() {
    let msg = compiled(&regex_condition(&[r"(?m)^foo$"], r#","case":true"#));
    assert!(!msg.matches(b"\r\nfoo\r\n"), "\\r 不被当作行尾分隔");
}

#[test]
fn regex_multiline_crlf_anchor_matches() {
    let msg = compiled(&regex_condition(&[r"(?mR)^foo$"], r#","case":true"#));
    assert!(msg.matches(b"\r\nfoo\r\n"), "回车换行整体视为一处行边界");
}

#[test]
fn regex_non_unicode_word_class_ascii_only() {
    let data = lines_data(&[
        "11-14 12:00:00.000  100  200 I T: \u{4e2d}\u{6587}\u{8bcd}",
        "11-14 12:00:01.000  100  200 I T: ascii",
    ]);
    assert_eq!(
        scan_rows(&regex_condition(&[r"(?-u)\w+"], ""), "re-no-uni", &data),
        vec![1]
    );
    assert_eq!(
        scan_rows(&regex_condition(&[r"\w+"], ""), "re-with-uni", &data),
        vec![0, 1]
    );
}

#[test]
fn regex_non_unicode_property_class_rejected() {
    let err = reject(&regex_condition(&[r"(?-u)\p{Lu}"], ""));
    assert_eq!(reason(&err), "syntax");
    assert_eq!(index(&err), Some(0));

    let err = reject(&regex_condition(&[r"(?-u:\W)"], ""));
    assert_eq!(reason(&err), "syntax");
}
