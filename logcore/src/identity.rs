//! 四要素身份（D14 / D18）：size、mtime、首尾各 64 KB FNV-1a 指纹。
//! 时间戳以 Windows FILETIME ticks 保存，线格式用 Unix epoch 毫秒。

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;
use std::time::UNIX_EPOCH;

pub const FINGERPRINT_BYTES: u64 = 64 * 1024;
pub const FILETIME_UNIX_EPOCH_TICKS: u64 = 116_444_736_000_000_000;

#[derive(Clone, PartialEq, Eq, Debug)]
pub struct Identity {
    pub size: u64,
    pub mtime_ticks: u64,
    pub head_hash: u64,
    pub tail_hash: u64,
}

impl Identity {
    pub fn capture(path: &Path) -> std::io::Result<Self> {
        let meta = std::fs::metadata(path)?;
        let size = meta.len();
        let mtime_ticks = meta
            .modified()
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_secs() * 10_000_000 + d.subsec_nanos() as u64 / 100)
            .unwrap_or(0)
            + FILETIME_UNIX_EPOCH_TICKS;

        let mut file = File::open(path)?;
        let len = FINGERPRINT_BYTES.min(size);
        let mut head = vec![0u8; len as usize];
        if len > 0 {
            file.read_exact(&mut head)?;
        }
        let head_hash = fnv1a(&head);
        let tail_hash = if size > len {
            file.seek(SeekFrom::Start(size - len))?;
            let mut tail = vec![0u8; len as usize];
            file.read_exact(&mut tail)?;
            fnv1a(&tail)
        } else {
            // 小文件首尾是同一段（既有实现口径）
            head_hash
        };

        Ok(Self {
            size,
            mtime_ticks,
            head_hash,
            tail_hash,
        })
    }

    pub fn mtime_ms(&self) -> u64 {
        self.mtime_ticks.saturating_sub(FILETIME_UNIX_EPOCH_TICKS) / 10_000
    }

    pub fn head_hex(&self) -> String {
        format!("{:016x}", self.head_hash)
    }

    pub fn tail_hex(&self) -> String {
        format!("{:016x}", self.tail_hash)
    }
}

pub fn fnv1a(data: &[u8]) -> u64 {
    let mut h: u64 = 14_695_981_039_346_656_037;
    for b in data {
        h ^= *b as u64;
        h = h.wrapping_mul(1_099_511_628_211);
    }
    h
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dir(name: &str) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!(
            "logcore-identity-{}-{}",
            std::process::id(),
            name
        ));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn identity_four_elements_stable() {
        let d = dir("stable");
        let p = d.join("a.log");
        std::fs::write(&p, b"hello world").unwrap();
        let a = Identity::capture(&p).unwrap();
        let b = Identity::capture(&p).unwrap();
        assert_eq!(a, b);
        assert_eq!(a.size, 11);
        assert_eq!(a.head_hash, a.tail_hash, "小文件复用头哈希");
        assert_eq!(a.head_hex().len(), 16);
        assert_eq!(a.tail_hex(), a.head_hex());
        assert_eq!(fnv1a(b""), 14_695_981_039_346_656_037);
    }

    #[test]
    fn identity_tail_samples_last_64k() {
        let d = dir("tail");
        let p = d.join("big.log");
        let mut bytes = vec![b'a'; 200 * 1024];
        std::fs::write(&p, &bytes).unwrap();
        let before = Identity::capture(&p).unwrap();
        // 只改中间（首段 0..64 KB 与尾段 136 KB..200 KB 之外）→ 指纹不变
        bytes[100 * 1024] = b'z';
        std::fs::write(&p, &bytes).unwrap();
        let after = Identity::capture(&p).unwrap();
        assert_eq!(before.head_hash, after.head_hash);
        assert_eq!(before.tail_hash, after.tail_hash);
        assert_eq!(before.size, after.size);
        // 改尾部 → 尾指纹变化
        let len = bytes.len();
        bytes[len - 1] = b'y';
        std::fs::write(&p, &bytes).unwrap();
        let changed = Identity::capture(&p).unwrap();
        assert_ne!(changed.tail_hash, after.tail_hash);
    }

    #[test]
    fn identity_mtime_ms_matches_epoch() {
        let d = dir("mtime");
        let p = d.join("a.log");
        std::fs::write(&p, b"x").unwrap();
        let id = Identity::capture(&p).unwrap();
        let now = std::time::SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64;
        assert!(id.mtime_ms() <= now + 1000);
        assert!(id.mtime_ms() + 86_400_000 > now, "mtime 应接近当前时间");
    }

    #[test]
    fn identity_empty_is_legal_combination() {
        // 空 identity + 缓存未命中 = 合法组合（由描述类型承载，见 descriptor 测试）
        let d = dir("empty");
        let p = d.join("e.log");
        std::fs::write(&p, b"").unwrap();
        let id = Identity::capture(&p).unwrap();
        assert_eq!(id.size, 0);
        assert_eq!(id.head_hex(), format!("{:016x}", fnv1a(b"")));
    }
}