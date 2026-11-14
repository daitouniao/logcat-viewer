//! logcore：源获取契约的 Rust 实现切片。
//!
//! 分层：判定与描述（`request` / `probe` / `route` / `identity` / `descriptor`）→
//! 获取与装载（`source` / `proxy` / `parse` / `sidecar`）→ 事件与进度（`events`）→
//! FFI 面（`ffi`）。宿主只经 `ffi` 的 C ABI 访问本 crate。

pub mod descriptor;
pub mod error;
pub mod events;
pub mod ffi;
pub mod identity;
pub mod locks;
pub mod maintain;
pub mod parse;
pub mod probe;
pub mod proxy;
pub mod request;
pub mod route;
pub mod sidecar;
pub mod source;
pub mod state;

#[cfg(windows)]
pub mod winserver;

#[cfg(test)]
pub mod proxy_support;

#[cfg(test)]
pub mod testutil;