//! sidecar 索引缓存（design D18）：键派生沿用既有规则，字节格式冻结 v1。
//! 命中判定在 plan 阶段只读完成；写入临时文件 + 原子替换；容量回收保留在用条目。

use std::collections::HashMap;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::SystemTime;

use crate::identity::{fnv1a, Identity};

pub const FORMAT_VERSION: i32 = 1;
pub const SEGMENT_COUNT: usize = 8;
pub const WIDTHS: [u8; 8] = [8, 4, 4, 1, 4, 4, 1, 4];
pub const MAGIC: [u8; 4] = *b"LCSC";
const CRASH_ENTRY_SIZE: usize = 21;
const MAX_TABLE_COUNT: i32 = 1 << 24;
const MAX_STRING_BYTES: i32 = 1 << 20;
const TEMP_INFIX: &str = ".tmp-";
static TEMP_SEQ: AtomicU64 = AtomicU64::new(0);
pub const EXTENSION: &str = ".lcs";
const MAX_PREFIX_LEN: usize = 24;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CrashEntry {
    pub row: i32,
    pub exc_id: i32,
    pub pkg_id: i32,
    pub pid: i32,
    pub tid: i32,
    pub kind: u8,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Payload {
    pub offs: Vec<i64>,
    pub lens: Vec<i32>,
    pub moff: Vec<i32>,
    pub minutes: Vec<u8>,
    pub pids: Vec<i32>,
    pub tids: Vec<i32>,
    pub lvls: Vec<u8>,
    pub tag_ids: Vec<i32>,
    pub tags: Vec<String>,
    pub exc_types: Vec<String>,
    pub pkgs: Vec<String>,
    pub crashes: Vec<CrashEntry>,
}

impl Payload {
    pub fn row_count(&self) -> usize {
        self.offs.len()
    }

    fn segment_lengths(&self) -> [i64; 8] {
        [
            (self.offs.len() * WIDTHS[0] as usize) as i64,
            (self.lens.len() * WIDTHS[1] as usize) as i64,
            (self.moff.len() * WIDTHS[2] as usize) as i64,
            (self.minutes.len() * WIDTHS[3] as usize) as i64,
            (self.pids.len() * WIDTHS[4] as usize) as i64,
            (self.tids.len() * WIDTHS[5] as usize) as i64,
            (self.lvls.len() * WIDTHS[6] as usize) as i64,
            (self.tag_ids.len() * WIDTHS[7] as usize) as i64,
        ]
    }
}

fn dict_size(items: &[String]) -> i64 {
    let mut n = 4i64;
    for s in items {
        n += 4 + s.len() as i64;
    }
    n
}

fn header_size(p: &Payload) -> i64 {
    4 // magic
        + 4 // version
        + 4 // rows
        + SEGMENT_COUNT as i64 // widths
        + SEGMENT_COUNT as i64 * 8
        + 8 // total
        + 8 + 8 + 8 + 8 // identity
        + dict_size(&p.tags)
        + dict_size(&p.exc_types)
        + dict_size(&p.pkgs)
        + 4
        + (CRASH_ENTRY_SIZE as i64) * p.crashes.len() as i64
}

pub fn encode(identity: &Identity, payload: &Payload, version: i32) -> Vec<u8> {
    let mut out = Vec::new();
    write_to(&mut out, identity, payload, version).expect("内存写入不失败");
    out
}

/// 流式写成 v1 字节：头部一次算齐，随后逐字典与逐列写出；
/// 结果与 [`encode`] 逐字节相同。
pub fn write_to<W: Write>(
    w: &mut W,
    identity: &Identity,
    payload: &Payload,
    version: i32,
) -> std::io::Result<()> {
    let seg = payload.segment_lengths();
    let total = header_size(payload) + seg.iter().sum::<i64>();
    w.write_all(&MAGIC)?;
    w.write_all(&version.to_le_bytes())?;
    w.write_all(&(payload.row_count() as i32).to_le_bytes())?;
    w.write_all(&WIDTHS)?;
    for len in seg {
        w.write_all(&len.to_le_bytes())?;
    }
    w.write_all(&total.to_le_bytes())?;
    w.write_all(&(identity.size as i64).to_le_bytes())?;
    w.write_all(&(identity.mtime_ticks as i64).to_le_bytes())?;
    w.write_all(&identity.head_hash.to_le_bytes())?;
    w.write_all(&identity.tail_hash.to_le_bytes())?;
    for dict in [&payload.tags, &payload.exc_types, &payload.pkgs] {
        w.write_all(&(dict.len() as i32).to_le_bytes())?;
        for s in dict {
            let bytes = s.as_bytes();
            w.write_all(&(bytes.len() as i32).to_le_bytes())?;
            w.write_all(bytes)?;
        }
    }
    w.write_all(&(payload.crashes.len() as i32).to_le_bytes())?;
    for c in &payload.crashes {
        for v in [c.row, c.exc_id, c.pkg_id, c.pid, c.tid] {
            w.write_all(&v.to_le_bytes())?;
        }
        w.write_all(&[c.kind])?;
    }
    write_i64s(w, &payload.offs)?;
    write_i32s(w, &payload.lens)?;
    write_i32s(w, &payload.moff)?;
    w.write_all(&payload.minutes)?;
    write_i32s(w, &payload.pids)?;
    write_i32s(w, &payload.tids)?;
    w.write_all(&payload.lvls)?;
    write_i32s(w, &payload.tag_ids)?;
    Ok(())
}

const WRITE_CHUNK: usize = 8192;

fn write_i64s<W: Write>(w: &mut W, values: &[i64]) -> std::io::Result<()> {
    let mut buf = [0u8; WRITE_CHUNK * 8];
    for chunk in values.chunks(WRITE_CHUNK) {
        for (i, v) in chunk.iter().enumerate() {
            buf[i * 8..i * 8 + 8].copy_from_slice(&v.to_le_bytes());
        }
        w.write_all(&buf[..chunk.len() * 8])?;
    }
    Ok(())
}

fn write_i32s<W: Write>(w: &mut W, values: &[i32]) -> std::io::Result<()> {
    let mut buf = [0u8; WRITE_CHUNK * 4];
    for chunk in values.chunks(WRITE_CHUNK) {
        for (i, v) in chunk.iter().enumerate() {
            buf[i * 4..i * 4 + 4].copy_from_slice(&v.to_le_bytes());
        }
        w.write_all(&buf[..chunk.len() * 4])?;
    }
    Ok(())
}

struct Cursor<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Cursor<'a> {
    fn take(&mut self, n: usize) -> Option<&'a [u8]> {
        if self.pos + n > self.data.len() {
            return None;
        }
        let s = &self.data[self.pos..self.pos + n];
        self.pos += n;
        Some(s)
    }
    fn i32(&mut self) -> Option<i32> {
        self.take(4).map(|b| i32::from_le_bytes(b.try_into().unwrap()))
    }
    fn i64(&mut self) -> Option<i64> {
        self.take(8).map(|b| i64::from_le_bytes(b.try_into().unwrap()))
    }
    fn u64(&mut self) -> Option<u64> {
        self.take(8).map(|b| u64::from_le_bytes(b.try_into().unwrap()))
    }
    fn u8(&mut self) -> Option<u8> {
        self.take(1).map(|b| b[0])
    }
}

/// 定长头部前缀：magic + version + rows + widths + 8 段长 + total + 身份四要素。
pub const HEADER_SIZE: usize = 4 + 4 + 4 + SEGMENT_COUNT + SEGMENT_COUNT * 8 + 8 + 32;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Header {
    pub rows: i32,
    pub total: i64,
    pub identity: Identity,
}

/// 解析定长头部（124 字节已读入），返回 (rows, 列数据总长, total, 身份)。
fn parse_fixed_header(c: &mut Cursor) -> Result<(i32, i64, i64, Identity), String> {
    if c.take(4) != Some(&MAGIC[..]) {
        return Err("magic 不符".into());
    }
    let version = c.i32().ok_or("头部截断")?;
    if version != FORMAT_VERSION {
        return Err(format!("格式版本不识别（{version}）"));
    }
    let rows = c.i32().ok_or("头部截断")?;
    if rows < 0 {
        return Err("行数为负".into());
    }
    for w in WIDTHS {
        if c.u8() != Some(w) {
            return Err("段元素宽度不符".into());
        }
    }
    let mut data_length = 0i64;
    for i in 0..SEGMENT_COUNT {
        let len = c.i64().ok_or("头部截断")?;
        if len != rows as i64 * WIDTHS[i] as i64 {
            return Err(format!("段长度不符（第 {i} 段）"));
        }
        data_length += len;
    }
    let total = c.i64().ok_or("头部截断")?;
    let size = c.i64().ok_or("头部截断")?;
    let mtime = c.i64().ok_or("头部截断")?;
    let head_hash = c.u64().ok_or("头部截断")?;
    let tail_hash = c.u64().ok_or("头部截断")?;
    Ok((
        rows,
        data_length,
        total,
        Identity {
            size: size as u64,
            mtime_ticks: mtime as u64,
            head_hash,
            tail_hash,
        },
    ))
}

/// 只读定长头部做命中判定：魔数 / 版本 / 行数 / 段长 / 总长 / 文件长度 / 身份。
/// 通过不代表列数据完好——解码在装载开始进行，失败再降级。
pub fn probe_header(bytes: &[u8], file_len: u64, expected: &Identity) -> Option<Header> {
    if bytes.len() < HEADER_SIZE {
        return None;
    }
    let mut c = Cursor { data: bytes, pos: 0 };
    let (rows, data_len, total, identity) = parse_fixed_header(&mut c).ok()?;
    if total < HEADER_SIZE as i64 + data_len || total as u64 != file_len {
        return None;
    }
    if &identity != expected {
        return None;
    }
    Some(Header {
        rows,
        total,
        identity,
    })
}

/// 读出并校验；`expected = None` 时只做结构校验（维护入口用）。
pub fn decode(bytes: &[u8], expected: Option<&Identity>) -> Result<Payload, String> {
    decode_reader(&mut &bytes[..], expected)
}

/// 从文件流式解码：只读头部与字典/崩溃表，再依段长逐列读入，不整份读取。
pub fn decode_file(path: &Path, expected: Option<&Identity>) -> Result<Payload, String> {
    let f = fs::File::open(path).map_err(|e| e.to_string())?;
    decode_reader(&mut std::io::BufReader::new(f), expected)
}

fn decode_reader<R: Read>(r: &mut R, expected: Option<&Identity>) -> Result<Payload, String> {
    let mut head = [0u8; HEADER_SIZE];
    r.read_exact(&mut head).map_err(|_| "头部截断".to_string())?;
    let mut c = Cursor {
        data: &head,
        pos: 0,
    };
    let (rows_i32, data_length, total, identity) = parse_fixed_header(&mut c)?;

    let tags = read_dict_r(r)?;
    let exc_types = read_dict_r(r)?;
    let pkgs = read_dict_r(r)?;

    let crash_count = read_i32_r(r).map_err(|_| "头部截断".to_string())?;
    if crash_count < 0 || crash_count > MAX_TABLE_COUNT || crash_count > rows_i32 {
        return Err("崩溃事件数不符".into());
    }
    let mut crashes = Vec::with_capacity(crash_count as usize);
    for _ in 0..crash_count {
        crashes.push(CrashEntry {
            row: read_i32_r(r)?,
            exc_id: read_i32_r(r)?,
            pkg_id: read_i32_r(r)?,
            pid: read_i32_r(r)?,
            tid: read_i32_r(r)?,
            kind: read_u8_r(r)?,
        });
    }

    let header_len = HEADER_SIZE as i64
        + dict_size(&tags)
        + dict_size(&exc_types)
        + dict_size(&pkgs)
        + 4
        + (CRASH_ENTRY_SIZE as i64) * crash_count as i64;
    if total != header_len + data_length {
        return Err("总长与头部声明不符".into());
    }
    if let Some(expected) = expected {
        if &identity != expected {
            return Err("源文件身份已变化".into());
        }
    }

    let rows = rows_i32 as usize;
    let mut payload = Payload {
        offs: read_i64s_r(r, rows)?,
        lens: read_i32s_r(r, rows)?,
        moff: read_i32s_r(r, rows)?,
        minutes: read_u8s_r(r, rows)?,
        pids: read_i32s_r(r, rows)?,
        tids: read_i32s_r(r, rows)?,
        lvls: read_u8s_r(r, rows)?,
        tag_ids: read_i32s_r(r, rows)?,
        tags,
        exc_types,
        pkgs,
        crashes,
    };
    // 字典 id 越界属结构损坏；-1 是「无 tag」的合法编码（与 C# 基线一致）
    for id in payload.tag_ids.iter() {
        if *id < -1 || (*id >= 0 && *id as usize >= payload.tags.len()) {
            return Err("tag id 越界".into());
        }
    }
    payload.crashes.retain(|c| c.row >= 0 && (c.row as usize) < payload.offs.len());
    Ok(payload)
}

fn read_i32_r<R: Read>(r: &mut R) -> Result<i32, String> {
    let mut b = [0u8; 4];
    r.read_exact(&mut b).map_err(|_| "数据截断".to_string())?;
    Ok(i32::from_le_bytes(b))
}

fn read_u8_r<R: Read>(r: &mut R) -> Result<u8, String> {
    let mut b = [0u8; 1];
    r.read_exact(&mut b).map_err(|_| "数据截断".to_string())?;
    Ok(b[0])
}

fn read_i64s_r<R: Read>(r: &mut R, n: usize) -> Result<Vec<i64>, String> {
    let mut buf = vec![0u8; n * 8];
    r.read_exact(&mut buf).map_err(|_| "数据截断".to_string())?;
    Ok(buf
        .chunks_exact(8)
        .map(|b| i64::from_le_bytes(b.try_into().unwrap()))
        .collect())
}

fn read_i32s_r<R: Read>(r: &mut R, n: usize) -> Result<Vec<i32>, String> {
    let mut buf = vec![0u8; n * 4];
    r.read_exact(&mut buf).map_err(|_| "数据截断".to_string())?;
    Ok(buf
        .chunks_exact(4)
        .map(|b| i32::from_le_bytes(b.try_into().unwrap()))
        .collect())
}

fn read_u8s_r<R: Read>(r: &mut R, n: usize) -> Result<Vec<u8>, String> {
    let mut buf = vec![0u8; n];
    r.read_exact(&mut buf).map_err(|_| "数据截断".to_string())?;
    Ok(buf)
}

fn read_dict_r<R: Read>(r: &mut R) -> Result<Vec<String>, String> {
    let count = read_i32_r(r).map_err(|_| "头部截断".to_string())?;
    if count < 0 || count > MAX_TABLE_COUNT {
        return Err("字典条目数不符".into());
    }
    let mut out = Vec::with_capacity(count as usize);
    for _ in 0..count {
        let len = read_i32_r(r).map_err(|_| "头部截断".to_string())?;
        if len < 0 || len > MAX_STRING_BYTES {
            return Err("字典条目长度不符".into());
        }
        let mut bytes = vec![0u8; len as usize];
        r.read_exact(&mut bytes).map_err(|_| "数据截断".to_string())?;
        out.push(String::from_utf8(bytes).map_err(|_| "字典非 UTF-8".to_string())?);
    }
    Ok(out)
}

/// 键派生：可读前缀（文件名主干，≤24，非法字符→`_`）+ 全路径大写 FNV-1a 的 16 位小写十六进制。
pub fn derive_name(source_path: &str) -> String {
    let normalized = normalize_for_key(source_path);
    let stem = file_stem(&normalized);
    let prefix = sanitize_prefix(&stem);
    let upper: String = normalized.to_uppercase();
    let hash = fnv1a(upper.as_bytes());
    format!("{prefix}_{hash:016x}{EXTENSION}")
}

/// 仅用于键派生：统一分隔符（与 C# `Path.GetFullPath` 的可读输出对齐）。
fn normalize_for_key(path: &str) -> String {
    path.replace('/', "\\")
}

fn file_stem(path: &str) -> String {
    let name = path.rsplit('\\').next().unwrap_or(path);
    match name.rfind('.') {
        Some(i) if i > 0 => name[..i].to_string(),
        _ => name.to_string(),
    }
}

fn sanitize_prefix(stem: &str) -> String {
    let mut out = String::new();
    for ch in stem.chars() {
        if out.chars().count() >= MAX_PREFIX_LEN {
            break;
        }
        out.push(if ch.is_alphanumeric() { ch } else { '_' });
    }
    if out.is_empty() {
        "log".to_string()
    } else {
        out
    }
}

/// 在用条目登记（进程级）：同一缓存目录可能被多个 `CacheStore`（每个源一份）持有，
/// 淘汰时必须以全局视图保护正在使用的条目。
fn in_use_registry() -> &'static Mutex<HashMap<PathBuf, usize>> {
    static REG: std::sync::OnceLock<Mutex<HashMap<PathBuf, usize>>> = std::sync::OnceLock::new();
    REG.get_or_init(|| Mutex::new(HashMap::new()))
}

/// 缓存目录治理。
pub struct CacheStore {
    pub dir: PathBuf,
    entry_limit_bytes: u64,
    dir_limit_bytes: u64,
}

impl CacheStore {
    pub fn new(dir: PathBuf, entry_limit_bytes: u64, dir_limit_bytes: u64) -> Self {
        Self {
            dir,
            entry_limit_bytes,
            dir_limit_bytes,
        }
    }

    pub fn entry_path(&self, source_path: &str) -> PathBuf {
        self.dir.join(derive_name(source_path))
    }

    /// 装载开始解码条目（流式）；损坏返回 None（调用方降级完整解析）。
    pub fn load(&self, source_path: &str, identity: &Identity) -> Option<Payload> {
        decode_file(&self.entry_path(source_path), Some(identity)).ok()
    }

    /// plan 头部探测：只读定长头部（不读列数据），通过即登记在用。
    pub fn probe_header(&self, source_path: &str, identity: &Identity) -> Option<Header> {
        let entry = self.entry_path(source_path);
        let file_len = fs::metadata(&entry).ok()?.len();
        let mut f = fs::File::open(&entry).ok()?;
        let mut buf = vec![0u8; HEADER_SIZE];
        f.read_exact(&mut buf).ok()?;
        let header = probe_header(&buf, file_len, identity)?;
        self.mark_in_use(source_path);
        Some(header)
    }

    pub fn mark_in_use(&self, source_path: &str) {
        let key = self.entry_path(source_path);
        *in_use_registry().lock().unwrap().entry(key).or_insert(0) += 1;
    }

    pub fn unmark_in_use(&self, source_path: &str) {
        let key = self.entry_path(source_path);
        let mut guard = in_use_registry().lock().unwrap();
        if let Some(count) = guard.get_mut(&key) {
            *count -= 1;
            if *count == 0 {
                guard.remove(&key);
            }
        }
    }

    fn is_in_use(&self, name: &str) -> bool {
        in_use_registry()
            .lock()
            .unwrap()
            .contains_key(&self.dir.join(name))
    }

    /// 落盘：静态文件边界（before == after）、单条上限、容量淘汰、临时文件 + 原子替换。
    pub fn save(
        &self,
        source_path: &str,
        before: Option<&Identity>,
        after: &Identity,
        payload: &Payload,
    ) -> bool {
        if before != Some(after) {
            return false;
        }
        let estimated = payload.row_count() as u64 * 30;
        if estimated > self.entry_limit_bytes {
            return false;
        }
        if fs::create_dir_all(&self.dir).is_err() {
            return false;
        }
        let entry = self.entry_path(source_path);

        // 更高版本的条目不被覆盖
        if let Ok(mut f) = fs::File::open(&entry) {
            let mut head = [0u8; 8];
            if f.read_exact(&mut head).is_ok() && head[..4] == MAGIC {
                let version = i32::from_le_bytes(head[4..8].try_into().unwrap());
                if version > FORMAT_VERSION {
                    return false;
                }
            }
        }

        self.evict_for_space(estimated, &entry);

        let temp = temp_path_for(&entry);
        let written = (|| -> std::io::Result<()> {
            let mut w = std::io::BufWriter::new(fs::File::create(&temp)?);
            write_to(&mut w, after, payload, FORMAT_VERSION)?;
            w.flush()?;
            let f = w.into_inner().map_err(|e| e.into_error())?;
            f.sync_all()?;
            Ok(())
        })();
        if written.is_err() {
            let _ = fs::remove_file(&temp);
            return false;
        }
        if fs::rename(&temp, &entry).is_err() {
            let _ = fs::remove_file(&temp);
            return false;
        }
        touch(&entry);
        true
    }

    fn evict_for_space(&self, incoming: u64, new_entry: &Path) {
        let mut entries: Vec<(PathBuf, String, SystemTime, u64)> = Vec::new();
        let mut total = 0u64;
        let Ok(read) = fs::read_dir(&self.dir) else {
            return;
        };
        for e in read.flatten() {
            let path = e.path();
            if path.extension().and_then(|s| s.to_str()) != Some(&EXTENSION[1..]) {
                continue;
            }
            let Ok(meta) = e.metadata() else { continue };
            let name = e.file_name().to_string_lossy().to_string();
            total += meta.len();
            entries.push((path, name, last_used(&meta), meta.len()));
        }
        if total + incoming <= self.dir_limit_bytes {
            return;
        }
        entries.sort_by_key(|e| e.2);
        for (path, name, _, len) in entries {
            if total + incoming <= self.dir_limit_bytes {
                break;
            }
            if path == new_entry {
                continue;
            }
            if self.is_in_use(&name) {
                continue;
            }
            if fs::remove_file(&path).is_ok() {
                total = total.saturating_sub(len);
            }
        }
    }
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

fn touch(entry: &Path) {
    let _ = fs::OpenOptions::new().write(true).open(entry);
}

/// 临时文件名：条目名 + 进程内唯一后缀（原子计数），避免同进程并发写相撞。
fn temp_path_for(entry: &Path) -> PathBuf {
    let seq = TEMP_SEQ.fetch_add(1, Ordering::Relaxed);
    entry.with_file_name(format!(
        "{}{}{}",
        entry.file_name().unwrap().to_string_lossy(),
        TEMP_INFIX,
        seq
    ))
}

/// 测试用：某缓存目录内的在用条目数（验证登记与注销配对）。
#[cfg(test)]
pub fn in_use_len(dir: &Path) -> usize {
    in_use_registry()
        .lock()
        .unwrap()
        .keys()
        .filter(|k| k.parent() == Some(dir))
        .count()
}

/// 维护口径：条目是否能被当前实现完整解析（结构校验用）。版本高于当前实现视为
/// 结构可读的有效条目（不得删除），只认魔数 / 版本段 / 段长 / 总长等结构错误。
pub fn structurally_readable(bytes: &[u8]) -> bool {
    if bytes.len() < 8 || bytes[..4] != MAGIC {
        return false;
    }
    let version = i32::from_le_bytes(bytes[4..8].try_into().unwrap());
    if version <= 0 {
        return false;
    }
    if version != FORMAT_VERSION {
        // 版本不符但头部可读：结构有效，保留（低版本由装载时原位重写，高版本不得覆盖）
        return true;
    }
    decode(bytes, None).is_ok()
}

pub const DEFAULT_ENTRY_BYTES_PER_ROW: u64 = 30;

#[cfg(test)]
pub mod tests {
    use super::*;
    use crate::identity::FILETIME_UNIX_EPOCH_TICKS;

    pub fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "logcore-sidecar-{}-{}-{}",
            std::process::id(),
            name,
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    pub fn sample_identity() -> Identity {
        Identity {
            size: 123_456,
            mtime_ticks: 133_000_000_000_000_000,
            head_hash: 0x0123_4567_89ab_cdef,
            tail_hash: 0xfedc_ba98_7654_3210,
        }
    }

    pub fn sample_payload() -> Payload {
        Payload {
            offs: vec![0, 10, 25],
            lens: vec![10, 15, 7],
            moff: vec![0, 1000, 2000],
            minutes: vec![5, 6, 7],
            pids: vec![100, 101, 100],
            tids: vec![200, 201, 200],
            lvls: vec![4, 5, 6],
            tag_ids: vec![0, 1, 0],
            tags: vec!["Main".into(), "Worker".into()],
            exc_types: vec![],
            pkgs: vec!["com.example".into()],
            crashes: vec![CrashEntry {
                row: 2,
                exc_id: 0,
                pkg_id: 0,
                pid: 100,
                tid: 200,
                kind: 1,
            }],
        }
    }

    fn golden_path(name: &str) -> PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("testdata").join(name)
    }

    fn write_state(dir: &Path, data: &[u8]) -> PathBuf {
        let p = dir.join("source.log");
        fs::write(&p, data).unwrap();
        p
    }

    #[test]
    fn sidecar_key_matches_golden_vectors() {
        let text = fs::read_to_string(golden_path("sidecar_keys.json")).unwrap();
        let vec: serde_json::Value = serde_json::from_str(&text).unwrap();
        for item in vec.as_array().unwrap() {
            let path = item["path"].as_str().unwrap();
            let entry = item["entry"].as_str().unwrap();
            assert_eq!(derive_name(path), entry, "键派生与 C# 实现不一致：{path}");
        }
    }

    #[test]
    fn sidecar_bytes_match_golden_dump() {
        let golden = fs::read(golden_path("sidecar_sample.bin")).unwrap();
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        assert_eq!(bytes, golden, "sidecar 字节与 C# 实现不一致");
        let decoded = decode(&golden, Some(&sample_identity())).unwrap();
        assert_eq!(decoded, sample_payload());
    }

    #[test]
    fn header_probe_accepts_valid() {
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        let h = probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &sample_identity())
            .expect("合法头部应通过");
        assert_eq!(h.rows, 3);
        assert_eq!(h.total, bytes.len() as i64);
    }

    #[test]
    fn header_probe_rejects_bad_magic() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[0] = b'X';
        assert!(probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &sample_identity()).is_none());
    }

    #[test]
    fn header_probe_rejects_version() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[4..8].copy_from_slice(&(FORMAT_VERSION + 1).to_le_bytes());
        assert!(probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &sample_identity()).is_none());
    }

    #[test]
    fn header_probe_rejects_negative_rows() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[8..12].copy_from_slice(&(-1i32).to_le_bytes());
        assert!(probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &sample_identity()).is_none());
    }

    #[test]
    fn header_probe_rejects_seg_len_mismatch() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        // 段长区起点 = magic 4 + version 4 + rows 4 + widths 8
        bytes[20..28].copy_from_slice(&0i64.to_le_bytes());
        assert!(probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &sample_identity()).is_none());
    }

    #[test]
    fn header_probe_rejects_total_mismatch() {
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        assert!(
            probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64 + 1, &sample_identity()).is_none(),
            "文件长度与 total 不符"
        );
    }

    #[test]
    fn header_probe_rejects_identity_mismatch() {
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        let mut other = sample_identity();
        other.size += 1;
        assert!(probe_header(&bytes[..HEADER_SIZE], bytes.len() as u64, &other).is_none());
    }

    #[test]
    fn header_probe_reads_prefix_only() {
        let dir = temp_dir("header-prefix");
        fs::create_dir_all(&dir).unwrap();
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let src = "C:\\logs\\prefix.log".to_string();
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        for b in bytes[HEADER_SIZE..].iter_mut() {
            *b = 0xff; // 列数据全损坏：plan 仍命中、不入解析
        }
        fs::write(store.entry_path(&src), &bytes).unwrap();
        let h = store.probe_header(&src, &sample_identity()).expect("头部完好即命中");
        assert_eq!(h.rows, 3);
        assert_eq!(in_use_len(&dir), 1);
        store.unmark_in_use(&src);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn decode_rejects_bad_magic() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[0] = b'X';
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
    }

    #[test]
    fn decode_rejects_seg_len_mismatch() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[20..28].copy_from_slice(&1i64.to_le_bytes());
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
    }

    #[test]
    fn decode_rejects_total_mismatch() {
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        // total 起点 = magic 4 + version 4 + rows 4 + widths 8 + 8 段长 64
        let old = i64::from_le_bytes(bytes[84..92].try_into().unwrap());
        bytes[84..92].copy_from_slice(&(old + 1).to_le_bytes());
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
    }

    #[test]
    fn decode_rejects_dict_id_oob() {
        let mut payload = sample_payload();
        payload.tag_ids[0] = 99;
        let bytes = encode(&sample_identity(), &payload, FORMAT_VERSION);
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
    }

    #[test]
    fn encode_stream_roundtrip() {
        let dir = temp_dir("stream");
        let path = dir.join("entry.lcs");
        {
            let mut w = std::io::BufWriter::new(fs::File::create(&path).unwrap());
            write_to(&mut w, &sample_identity(), &sample_payload(), FORMAT_VERSION).unwrap();
            w.flush().unwrap();
        }
        let bytes = fs::read(&path).unwrap();
        assert_eq!(
            bytes,
            encode(&sample_identity(), &sample_payload(), FORMAT_VERSION),
            "流式写与整份编码逐字节一致"
        );
        assert_eq!(
            decode(&bytes, Some(&sample_identity())).unwrap(),
            sample_payload()
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn high_version_entry_not_overwritten() {
        let dir = temp_dir("high-version");
        fs::create_dir_all(&dir).unwrap();
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let src = "C:\\logs\\hv.log".to_string();
        let entry = store.entry_path(&src);
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes[4..8].copy_from_slice(&(FORMAT_VERSION + 1).to_le_bytes());
        fs::write(&entry, &bytes).unwrap();
        assert!(
            !store.save(
                &src,
                Some(&sample_identity()),
                &sample_identity(),
                &sample_payload()
            ),
            "高版本条目必须拒绝写入"
        );
        assert_eq!(fs::read(&entry).unwrap(), bytes, "高版本条目必须原样保留");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn sidecar_version_mismatch_misses() {
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION + 1);
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
        let bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION - 1);
        assert!(decode(&bytes, Some(&sample_identity())).is_err());
    }

    #[test]
    fn sidecar_corrupt_degrades_silently() {
        let dir = temp_dir("corrupt");
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let source = write_state(&dir, b"x");
        let src = source.to_string_lossy().to_string();
        let entry = store.entry_path(&src);
        fs::write(&entry, b"garbage").unwrap();
        assert!(store.probe_header(&src, &sample_identity()).is_none());
        // 结构损坏但 magic 正确、段长被截断
        let mut bytes = encode(&sample_identity(), &sample_payload(), FORMAT_VERSION);
        bytes.truncate(bytes.len() - 3);
        fs::write(&entry, &bytes).unwrap();
        assert!(store.probe_header(&src, &sample_identity()).is_none());
    }

    #[test]
    fn sidecar_identity_mismatch_misses() {
        let dir = temp_dir("identity");
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let id = sample_identity();
        let src = "C:\\logs\\identity.log".to_string();
        assert!(store.save(&src, Some(&id), &id, &sample_payload()));
        assert!(store.probe_header(&src, &id).is_some());
        let mut changed = id.clone();
        changed.size += 1;
        assert!(store.probe_header(&src, &changed).is_none());
        let mut changed = id.clone();
        changed.tail_hash ^= 1;
        assert!(
            store.probe_header(&src, &changed).is_none(),
            "尾指纹不一致必须未命中（证明读取了尾部指纹）"
        );
    }

    #[test]
    fn sidecar_plan_probe_writes_nothing() {
        let dir = temp_dir("probe-readonly");
        let cache = dir.join("cache");
        let store = CacheStore::new(cache.clone(), 1 << 30, 4 << 30);
        let src = "C:\\logs\\nothing.log".to_string();
        assert!(store.probe_header(&src, &sample_identity()).is_none());
        assert!(!cache.exists(), "plan 探测不得创建目录");
    }

    #[test]
    fn sidecar_plan_probe_reads_tail() {
        let dir = temp_dir("plan-tail");
        let store = CacheStore::new(dir.join("cache"), 1 << 30, 4 << 30);
        let mut bytes = vec![b'a'; 200 * 1024];
        let source = dir.join("tail.log");
        fs::write(&source, &bytes).unwrap();
        let src = source.to_string_lossy().to_string();
        let before = Identity::capture(&source).unwrap();
        assert!(store.save(&src, Some(&before), &before, &sample_payload()));
        assert!(store.probe_header(&src, &before).is_some());

        let last = bytes.len() - 1;
        bytes[last] = b'z';
        fs::write(&source, &bytes).unwrap();
        let after = Identity::capture(&source).unwrap();
        assert_eq!(before.head_hash, after.head_hash, "首段未变");
        assert_ne!(before.tail_hash, after.tail_hash, "尾部指纹必须变化");
        assert!(store.probe_header(&src, &after).is_none(), "尾部变化必须未命中");

        let mut forced = before.clone();
        forced.tail_hash = after.tail_hash;
        assert!(
            store.probe_header(&src, &forced).is_none(),
            "仅尾指纹变化也必须未命中（证明探测读取了尾部）"
        );
    }

    #[test]
    fn sidecar_atomic_replace() {
        let dir = temp_dir("atomic");
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let id = sample_identity();
        let src = "C:\\logs\\atomic.log".to_string();
        assert!(store.save(&src, Some(&id), &id, &sample_payload()));
        let entry = store.entry_path(&src);
        assert!(entry.exists());
        let leftovers: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .flatten()
            .filter(|e| e.file_name().to_string_lossy().contains(TEMP_INFIX))
            .collect();
        assert!(leftovers.is_empty(), "不得留下写入残留");
        // 覆盖写仍保持有效
        assert!(store.save(&src, Some(&id), &id, &sample_payload()));
        assert!(store.probe_header(&src, &id).is_some());
    }

    #[test]
    fn sidecar_eviction_keeps_in_use() {
        let dir = temp_dir("evict");
        // 目录上限只够放两个条目；单条 3 行 × 30 = 90 字节
        let payload = sample_payload();
        let entry_len = encode(&sample_identity(), &payload, FORMAT_VERSION).len() as u64;
        let store = CacheStore::new(dir.clone(), 1 << 30, entry_len * 2);
        let id = sample_identity();
        let a = "C:\\logs\\a.log".to_string();
        let b = "C:\\logs\\b.log".to_string();
        let c = "C:\\logs\\c.log".to_string();
        assert!(store.save(&a, Some(&id), &id, &payload));
        assert!(store.save(&b, Some(&id), &id, &payload));
        store.mark_in_use(&a);
        // 写 c 触发淘汰：b 更旧且不在用 → 删 b；a 在用 → 保留
        assert!(store.save(&c, Some(&id), &id, &payload));
        assert!(store.entry_path(&a).exists(), "在用条目必须保留");
        assert!(store.entry_path(&c).exists(), "新写条目必须保留");
        assert!(!store.entry_path(&b).exists(), "最旧的非在用条目应被淘汰");
    }

    #[test]
    fn sidecar_in_use_released_on_close() {
        let dir = temp_dir("in-use-release");
        let payload = sample_payload();
        let entry_len = encode(&sample_identity(), &payload, FORMAT_VERSION).len() as u64;
        let store = CacheStore::new(dir.clone(), 1 << 30, entry_len * 2);
        let id = sample_identity();
        let a = "C:\\logs\\a.log".to_string();
        let b = "C:\\logs\\b.log".to_string();
        let c = "C:\\logs\\c.log".to_string();
        store.save(&a, Some(&id), &id, &payload);
        store.save(&b, Some(&id), &id, &payload);
        store.mark_in_use(&a);
        store.unmark_in_use(&a);
        store.save(&c, Some(&id), &id, &payload);
        assert!(!store.entry_path(&a).exists(), "注销后应可被回收");
    }

    #[test]
    fn sidecar_recheck_after_plan_change() {
        let dir = temp_dir("recheck");
        let store = CacheStore::new(dir.clone(), 1 << 30, 4 << 30);
        let src = "C:\\logs\\recheck.log".to_string();
        let plan_id = Identity {
            size: 100,
            mtime_ticks: FILETIME_UNIX_EPOCH_TICKS + 1,
            head_hash: 1,
            tail_hash: 2,
        };
        assert!(store.save(&src, Some(&plan_id), &plan_id, &sample_payload()));
        // plan 命中
        assert!(store.probe_header(&src, &plan_id).is_some());
        // 文件在 plan 后被改：commit 复核身份不同 → 未命中
        let recheck = Identity {
            size: 101,
            ..plan_id.clone()
        };
        assert!(store.probe_header(&src, &recheck).is_none());
        assert!(store.save(&src, Some(&recheck), &recheck, &sample_payload()));
        assert!(store.probe_header(&src, &recheck).is_some());
    }

    #[test]
    fn sidecar_over_entry_limit_not_cached() {
        let dir = temp_dir("over-limit");
        let store = CacheStore::new(dir.clone(), 10, 4 << 30);
        let id = sample_identity();
        assert!(!store.save("C:\\logs\\big.log", Some(&id), &id, &sample_payload()));
    }
}