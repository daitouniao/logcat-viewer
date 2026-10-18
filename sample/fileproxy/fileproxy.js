#!/usr/bin/env node
/**
 * fileproxy.js — logcat 文件代理示例（Node.js，零第三方依赖）
 *
 * 场景：logcat 打不开/列不到的受限文件，由本脚本以任意手段读入，
 *       经 Windows 命名管道把字节流喂给应用。
 *
 * 用法：
 *   node fileproxy.js [--pipe <管道名>] [--root <目录>] [--idle <秒>]
 *     --pipe   默认 logcat-fileproxy（应用「设置-文件代理」里可改）
 *     --root   可选，只允许读该目录下的文件（演示最小安全边界；不给则不限制）
 *     --idle   可选，多少秒无客户端自动退出；0 = 永不（默认）
 *
 * 协议 v1：JSON 行控制消息（UTF-8）+ read 响应带长度前缀裸块，字节流模式。
 *   hello → {ok, proxy, proto}；open path → {ok, h, size}；
 *   read {h, off, len} → {ok, n} + n 字节裸块（n=0 即 EOF）；close {h} → {ok}。
 *   未知 op / 坏 JSON / 坏句柄回 {ok:false, err} 且不断线。
 */
'use strict';

const net = require('net');
const fs = require('fs');
const path = require('path');

function argOf(name, dflt) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : dflt;
}

const PIPE_NAME = argOf('pipe', 'logcat-fileproxy');
const ROOT = argOf('root', '');                       // '' = 不限制
const IDLE_SEC = parseInt(argOf('idle', '0'), 10) || 0;
const PIPE_PATH = '\\\\.\\pipe\\' + PIPE_NAME;
const MAX_READ = 16 * 1024 * 1024;                    // 单次 read 上限，与应用侧约定一致

const server = net.createServer((sock) => {
  sock.setNoDelay(true);
  let leftover = Buffer.alloc(0);
  let nextH = 1;
  const handles = new Map();                          // h -> fd
  let idleTimer = null;

  const bumpIdle = () => {
    if (IDLE_SEC <= 0) return;
    if (idleTimer) clearTimeout(idleTimer);
    idleTimer = setTimeout(() => process.exit(0), IDLE_SEC * 1000);
  };
  const sendJson = (obj) => sock.write(Buffer.from(JSON.stringify(obj) + '\n', 'utf8'));
  const deny = (id, err) => sendJson({ id, ok: false, err: String((err && err.message) || err) });

  function resolvePath(p) {
    if (typeof p !== 'string' || p.length === 0) throw new Error('bad path');
    if (ROOT) {
      const rel = path.relative(path.resolve(ROOT), path.resolve(p));
      if (rel === '..' || rel.startsWith('..' + path.sep) || path.isAbsolute(rel)) {
        throw new Error('path outside root');
      }
    }
    return p;
  }

  function handle(req) {
    const id = req.id || 0;
    bumpIdle();
    switch (req.op) {
      case 'hello':
        sendJson({ id, ok: true, proxy: 'node-fileproxy/1.0', proto: 1 });
        break;
      case 'stat':
        try {
          const st = fs.statSync(resolvePath(req.path));
          sendJson({ id, ok: true, size: st.size, mtime: Math.floor(st.mtimeMs / 1000) });
        } catch (e) { deny(id, e); }
        break;
      case 'open':
        try {
          const fd = fs.openSync(resolvePath(req.path), 'r');
          const h = nextH++;
          handles.set(h, fd);
          sendJson({ id, ok: true, h, size: fs.fstatSync(fd).size });
        } catch (e) { deny(id, e); }
        break;
      case 'read': {
        const fd = handles.get(req.h);
        if (fd === undefined) { deny(id, 'bad handle'); break; }
        // off/len 严禁用 `| 0`：按位或把 number 截成 32 位有符号整数，
        // off ≥ 2GB 时溢出为负 → Math.max 归 0 → 每次从头读（死循环）。
        // JSON number 安全整数到 2^53，fs.readSync 的 position 原生支持。
        const off = Math.max(0, Number(req.off) || 0);
        const len = Math.max(0, Math.min(MAX_READ, Number(req.len) || 0));
        const b = Buffer.alloc(len);
        try {
          const n = fs.readSync(fd, b, 0, len, off);
          sendJson({ id, ok: true, n });
          if (n > 0) sock.write(n === len ? b : b.subarray(0, n));
        } catch (e) { deny(id, e); }
        break;
      }
      case 'close': {
        const fd = handles.get(req.h);
        if (fd !== undefined) {
          try { fs.closeSync(fd); } catch (_) {}
          handles.delete(req.h);
        }
        sendJson({ id, ok: true });
        break;
      }
      default:
        deny(id, 'unknown op: ' + req.op);
    }
  }

  sock.on('data', (chunk) => {
    leftover = Buffer.concat([leftover, chunk]);
    let nl;
    while ((nl = leftover.indexOf(0x0a)) >= 0) {
      const line = leftover.subarray(0, nl).toString('utf8');
      leftover = leftover.subarray(nl + 1);
      if (line.trim().length === 0) continue;
      let req;
      try { req = JSON.parse(line); } catch (_) { deny(0, 'bad json'); continue; }
      try { handle(req); } catch (e) { deny(req.id || 0, e); }
    }
  });

  const cleanup = () => { for (const fd of handles.values()) { try { fs.closeSync(fd); } catch (_) {} } };
  sock.on('error', cleanup);
  sock.on('close', cleanup);
  bumpIdle();
});

server.listen(PIPE_PATH, () => {
  console.error('[fileproxy] listening on ' + PIPE_PATH);
});
server.on('error', (e) => {
  console.error('[fileproxy] listen failed: ' + e.message);
  process.exit(1);
});
