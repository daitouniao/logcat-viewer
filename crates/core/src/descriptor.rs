//! 源描述组装与线格式（design D14）：三轴正交、列声明、身份、租约与追加模式。

use serde_json::{json, Value};

use crate::identity::Identity;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Route {
    LocalDirect,
    ProxySpool,
    DeviceStream,
}

impl Route {
    pub fn wire(self) -> &'static str {
        match self {
            Route::LocalDirect => "LocalDirect",
            Route::ProxySpool => "ProxySpool",
            Route::DeviceStream => "DeviceStream",
        }
    }

    pub fn channel(self) -> &'static str {
        match self {
            Route::LocalDirect => "Local",
            Route::ProxySpool => "FileProxy",
            Route::DeviceStream => "AdbCapture",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LoadMode {
    OneShot,
    Progressive,
}

impl LoadMode {
    pub fn wire(self) -> &'static str {
        match self {
            LoadMode::OneShot => "OneShot",
            LoadMode::Progressive => "Progressive",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum AppendMode {
    Appendable,
    ReloadOnly,
}

impl AppendMode {
    pub fn wire(self) -> &'static str {
        match self {
            AppendMode::Appendable => "Appendable",
            AppendMode::ReloadOnly => "ReloadOnly",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LeaseKind {
    None,
    Ephemeral,
}

impl LeaseKind {
    pub fn wire(self) -> &'static str {
        match self {
            LeaseKind::None => "None",
            LeaseKind::Ephemeral => "Ephemeral",
        }
    }
}

#[derive(Clone, Debug)]
pub struct ColumnDef {
    pub id: &'static str,
    pub label_key: &'static str,
    pub width: u32,
}

/// 列标识跨版本稳定（宿主按 id 取值，不按位置）。
pub const COLUMNS: [ColumnDef; 6] = [
    ColumnDef { id: "time", label_key: "column.time", width: 150 },
    ColumnDef { id: "level", label_key: "column.level", width: 90 },
    ColumnDef { id: "pid", label_key: "column.pid", width: 70 },
    ColumnDef { id: "tid", label_key: "column.tid", width: 70 },
    ColumnDef { id: "tag", label_key: "column.tag", width: 160 },
    ColumnDef { id: "message", label_key: "column.message", width: 600 },
];

#[derive(Clone, Debug)]
pub struct Descriptor {
    pub route: Route,
    pub identity: Option<Identity>,
    pub load_mode: LoadMode,
    pub row_count: u64,
    pub doc_size: u64,
    pub lease_id: Option<String>,
    pub display_path: Option<String>,
    pub source_path: String,
}

impl Descriptor {
    pub fn skeleton(
        route: Route,
        identity: Option<Identity>,
        load_mode: LoadMode,
        display_path: Option<String>,
        source_path: String,
        lease_id: Option<String>,
    ) -> Self {
        Self {
            route,
            identity,
            load_mode,
            row_count: 0,
            doc_size: 0,
            lease_id,
            display_path,
            source_path,
        }
    }

    pub fn append_mode(&self) -> AppendMode {
        match self.route {
            Route::DeviceStream => AppendMode::Appendable,
            _ => AppendMode::ReloadOnly,
        }
    }

    pub fn lease_kind(&self) -> LeaseKind {
        match self.route {
            Route::LocalDirect => LeaseKind::None,
            // 暂存目录与采集文件均为关闭即删的临时产物（D13）
            Route::ProxySpool | Route::DeviceStream => LeaseKind::Ephemeral,
        }
    }

    fn identity_json(&self) -> Value {
        match &self.identity {
            None => Value::Null,
            Some(id) => json!({
                "size": id.size,
                "mtimeMs": id.mtime_ms(),
                "headHash": id.head_hex(),
                "tailHash": id.tail_hex(),
            }),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({
            "channel": self.route.channel(),
            "format": "Logcat",
            "encoding": "Utf8",
            "columns": COLUMNS.iter().map(|c| json!({
                "id": c.id,
                "labelKey": c.label_key,
                "width": c.width
            })).collect::<Vec<_>>(),
            "identity": self.identity_json(),
            "loadMode": self.load_mode.wire(),
            "rowCount": self.row_count,
            "docSize": self.doc_size,
            "appendMode": self.append_mode().wire(),
            "leaseKind": self.lease_kind().wire(),
            "leaseId": self.lease_id,
            "timeSemantics": "Original",
            "displayPath": self.display_path,
            "sourcePath": self.source_path,
        })
    }

    /// 包内校验：列声明顺序与稳定 id 一致（行/列读取不在本阶段导出面，D20）。
    pub fn validate_columns(&self) -> Result<(), String> {
        let ids: Vec<&str> = COLUMNS.iter().map(|c| c.id).collect();
        if ids != ["time", "level", "pid", "tid", "tag", "message"] {
            return Err("列 id 顺序漂移".to_string());
        }
        let mut seen = std::collections::HashSet::new();
        for c in &COLUMNS {
            if !seen.insert(c.id) {
                return Err(format!("列 id 重复：{}", c.id));
            }
            if !c.label_key.starts_with("column.") {
                return Err(format!("列 labelKey 非法：{}", c.label_key));
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::identity::{fnv1a, Identity};

    fn ident() -> Identity {
        Identity {
            size: 42,
            mtime_ticks: crate::identity::FILETIME_UNIX_EPOCH_TICKS + 1_700_000_000_000 * 10_000,
            head_hash: fnv1a(b"head"),
            tail_hash: fnv1a(b"tail"),
        }
    }

    fn d(route: Route) -> Descriptor {
        match route {
            Route::LocalDirect => Descriptor::skeleton(
                route,
                Some(ident()),
                LoadMode::Progressive,
                Some("C:\\logs\\a.log".into()),
                "C:\\logs\\a.log".into(),
                None,
            ),
            Route::ProxySpool => Descriptor::skeleton(
                route,
                None,
                LoadMode::Progressive,
                Some("C:\\logs\\a.log".into()),
                "C:\\temp\\logcat_spool_3\\a.log".into(),
                Some("logcat_spool_3".into()),
            ),
            Route::DeviceStream => Descriptor::skeleton(
                route,
                None,
                LoadMode::Progressive,
                None,
                "C:\\temp\\logcat_live_20261114_120000_4.log".into(),
                Some("logcat_live_20261114_120000_4".into()),
            ),
        }
    }

    #[test]
    fn descriptor_axes_orthogonal() {
        for route in [Route::LocalDirect, Route::ProxySpool, Route::DeviceStream] {
            let desc = d(route);
            let json = desc.to_json();
            assert_eq!(json["channel"], route.channel());
            // 三轴各自独立取值：渠道变化不牵动格式与编码
            assert_eq!(json["format"], "Logcat");
            assert_eq!(json["encoding"], "Utf8");
        }
        // 同一 route 下改身份/装载模式不改变渠道
        let mut a = d(Route::LocalDirect);
        a.load_mode = LoadMode::OneShot;
        assert_eq!(a.to_json()["channel"], "Local");
        assert_eq!(a.to_json()["loadMode"], "OneShot");
    }

    #[test]
    fn descriptor_columns_stable_ids() {
        let desc = d(Route::LocalDirect);
        let json = desc.to_json();
        let ids: Vec<&str> = json["columns"]
            .as_array()
            .unwrap()
            .iter()
            .map(|c| c["id"].as_str().unwrap())
            .collect();
        assert_eq!(ids, ["time", "level", "pid", "tid", "tag", "message"]);
        desc.validate_columns().unwrap();
        for c in json["columns"].as_array().unwrap() {
            assert!(c["labelKey"].as_str().unwrap().starts_with("column."));
            assert!(c["width"].as_u64().is_some());
        }
    }

    #[test]
    fn descriptor_display_and_source_path_split() {
        let desc = d(Route::ProxySpool);
        let json = desc.to_json();
        assert_eq!(json["displayPath"], "C:\\logs\\a.log");
        assert_eq!(json["sourcePath"], "C:\\temp\\logcat_spool_3\\a.log");
        assert_ne!(json["displayPath"], json["sourcePath"]);

        let local = d(Route::LocalDirect).to_json();
        assert_eq!(local["displayPath"], local["sourcePath"]);
    }

    #[test]
    fn descriptor_display_path_null_for_device() {
        let json = d(Route::DeviceStream).to_json();
        assert!(json["displayPath"].is_null());
        assert!(json["sourcePath"].as_str().unwrap().contains("logcat_live_"));
    }

    #[test]
    fn descriptor_json_matches_schema() {
        let json = d(Route::LocalDirect).to_json();
        let obj = json.as_object().unwrap();
        let expected = [
            "channel",
            "format",
            "encoding",
            "columns",
            "identity",
            "loadMode",
            "rowCount",
            "docSize",
            "appendMode",
            "leaseKind",
            "leaseId",
            "timeSemantics",
            "displayPath",
            "sourcePath",
        ];
        for key in expected {
            assert!(obj.contains_key(key), "缺字段 {key}");
        }
        assert_eq!(obj.len(), expected.len(), "不应多出未声明字段");
        assert_eq!(json["identity"]["size"], 42);
        assert_eq!(json["identity"]["headHash"].as_str().unwrap().len(), 16);
        assert_eq!(json["identity"]["mtimeMs"], 1_700_000_000_000u64);
        assert_eq!(json["rowCount"], 0);
        assert_eq!(json["docSize"], 0);
    }

    #[test]
    fn descriptor_lease_and_append_by_route() {
        let local = d(Route::LocalDirect).to_json();
        assert_eq!(local["appendMode"], "ReloadOnly");
        assert_eq!(local["leaseKind"], "None");
        assert!(local["leaseId"].is_null());

        let proxy = d(Route::ProxySpool).to_json();
        assert_eq!(proxy["appendMode"], "ReloadOnly");
        assert_eq!(proxy["leaseKind"], "Ephemeral");

        let device = d(Route::DeviceStream).to_json();
        assert_eq!(device["appendMode"], "Appendable");
        assert_eq!(device["leaseKind"], "Ephemeral", "采集文件关闭即删");
    }

    #[test]
    fn descriptor_lease_id_forms_by_route() {
        let proxy = d(Route::ProxySpool).to_json();
        assert!(proxy["leaseId"].as_str().unwrap().starts_with("logcat_spool_"));
        let device = d(Route::DeviceStream).to_json();
        assert!(device["leaseId"].as_str().unwrap().starts_with("logcat_live_"));
        // 采集文件名主干不含扩展名
        assert!(!device["leaseId"].as_str().unwrap().ends_with(".log"));
        let local = d(Route::LocalDirect).to_json();
        assert!(local["leaseId"].is_null());
    }

    #[test]
    fn descriptor_time_semantics_original() {
        for route in [Route::LocalDirect, Route::ProxySpool, Route::DeviceStream] {
            assert_eq!(d(route).to_json()["timeSemantics"], "Original");
        }
    }

    #[test]
    fn descriptor_identity_readable_at_accept() {
        // 受理骨架即给出完整身份（不因装载未完成而空缺）
        let desc = d(Route::LocalDirect);
        let json = desc.to_json();
        assert!(json["identity"].is_object());
        assert_eq!(json["rowCount"], 0);
        assert_eq!(json["loadMode"], "Progressive");
    }

    #[test]
    fn descriptor_identity_null_for_proxy() {
        for route in [Route::ProxySpool, Route::DeviceStream] {
            let json = d(route).to_json();
            assert!(json["identity"].is_null(), "{route:?} 身份应为整体 null");
        }
    }
}