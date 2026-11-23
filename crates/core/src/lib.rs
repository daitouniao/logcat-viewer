//! logcore_core：源获取契约的内核实现（无 FFI 面）。
//!
//! 分层：判定与描述（`request` / `probe` / `route` / `identity` / `descriptor`）→
//! 获取与装载（`source` / `proxy` / `parse` / `sidecar`）→ 事件与进度（`events`）。
//! C ABI 面在 `crates/cabi`，调试壳在 `crates/cli`。

pub mod crash;
pub mod descriptor;
pub mod error;
pub mod events;
pub mod filter;
pub mod format;
pub mod identity;
pub mod locks;
pub mod maintain;
pub mod page;
pub mod parse;
pub mod probe;
pub mod proxy;
pub mod request;
pub mod result;
pub mod route;
pub mod segment;
pub mod sidecar;
pub mod source;
pub mod state;
pub mod testutil;

#[cfg(any(test, feature = "test-support"))]
pub mod sample_gen;

#[cfg(windows)]
pub mod winserver;

#[cfg(test)]
pub mod proxy_support;
