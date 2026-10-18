#!/usr/bin/env python3
"""
fileproxy.py — logcat 文件代理示例（Python 3.8+，零第三方依赖）

场景：logcat 打不开/列不到的受限文件，由本脚本以任意手段读入，
     经 Windows 命名管道把字节流喂给应用。

用法：
    python fileproxy.py [--pipe logcat-fileproxy] [--root DIR] [--once]
      --pipe   默认 logcat-fileproxy（应用「设置-文件代理」里可改）
      --root   可选，只允许读该目录下的文件（演示最小安全边界；不给则不限制）
      --once   处理完一个客户端连接即退出（便于测试/一次性使用）

依赖：仅标准库。服务端用 CPython 内置 _winapi 创建命名管道（私有 API，
     但十余年稳定；生产建议换 pywin32 的 win32pipe，语义一致）。
     注意 idle 自动退出未实现——node 例子有 --idle 可参照。

协议 v1：JSON 行控制消息（UTF-8）+ read 响应带长度前缀裸块，字节流模式。
     hello → {ok, proxy, proto}；open path → {ok, h, size}；
     read {h, off, len} → {ok, n} + n 字节裸块（n=0 即 EOF）；close {h} → {ok}。
     未知 op / 坏 JSON / 坏句柄回 {ok:false, err} 且不断线。
"""
import _winapi
import json
import msvcrt
import os
import sys
import threading

PIPE_PREFIX = '\\\\.\\pipe\\'
# _winapi 只导出非零值的管道常量；BYTE 模式（type/readmode）在 Win32 中恒为 0，本地定义
# （PIPE_ACCESS_DUPLEX / PIPE_WAIT / ERROR_PIPE_CONNECTED / PIPE_UNLIMITED_INSTANCES 用 _winapi 的）。
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
MAX_READ = 16 * 1024 * 1024


def parse_args(argv):
    opts = {'pipe': 'logcat-fileproxy', 'root': '', 'once': False}
    i = 0
    while i < len(argv):
        if argv[i] == '--pipe' and i + 1 < len(argv):
            opts['pipe'] = argv[i + 1]; i += 2
        elif argv[i] == '--root' and i + 1 < len(argv):
            opts['root'] = argv[i + 1]; i += 2
        elif argv[i] == '--once':
            opts['once'] = True; i += 1
        else:
            i += 1
    return opts


ROOT = ''


class Conn:
    """一条已接通的管道连接：按行读 JSON，写 JSON 行 + 裸块。"""

    def __init__(self, f):
        self.f = f
        self.leftover = b''
        self.handles = {}

    # ── 读写原语 ──
    def readline(self):
        while b'\n' not in self.leftover:
            chunk = self.f.read(65536)
            if not chunk:
                raise ConnectionError('eof')
            self.leftover += chunk
        line, _, self.leftover = self.leftover.partition(b'\n')
        return line

    def send_json(self, obj):
        self.f.write((json.dumps(obj, ensure_ascii=False) + '\n').encode('utf-8'))

    def send_raw(self, data):
        self.f.write(data)

    def err(self, rid, e):
        self.send_json({'id': rid, 'ok': False, 'err': str(e)})

    # ── op 分发 ──
    def resolve(self, p):
        if not isinstance(p, str) or not p:
            raise ValueError('bad path')
        if ROOT:
            rel = os.path.relpath(os.path.abspath(p), ROOT)
            # 跨盘符时 relpath 返回绝对路径，'..' 两个判断都拦不住，必须补 isabs。
            if os.path.isabs(rel) or rel == '..' or rel.startswith('..' + os.sep):
                raise ValueError('path outside root')
        return p

    def handle(self, req):
        rid = req.get('id', 0)
        op = req.get('op')
        if op == 'hello':
            self.send_json({'id': rid, 'ok': True, 'proxy': 'py-fileproxy/1.0', 'proto': 1})
        elif op == 'stat':
            try:
                st = os.stat(self.resolve(req.get('path', '')))
                self.send_json({'id': rid, 'ok': True, 'size': st.st_size,
                                'mtime': int(st.st_mtime)})
            except OSError as e:
                self.err(rid, e)
        elif op == 'open':
            try:
                fd = os.open(self.resolve(req.get('path', '')),
                             os.O_RDONLY | os.O_BINARY)
                h = (max(self.handles) + 1) if self.handles else 1
                self.handles[h] = fd
                self.send_json({'id': rid, 'ok': True, 'h': h,
                                'size': os.fstat(fd).st_size})
            except OSError as e:
                self.err(rid, e)
        elif op == 'read':
            fd = self.handles.get(req.get('h'))
            if fd is None:
                self.err(rid, 'bad handle')
                return
            off = max(0, int(req.get('off', 0)))
            length = max(0, min(MAX_READ, int(req.get('len', 0))))
            try:
                # Windows 没有 os.pread（Unix 专属 API），用 lseek + read。
                os.lseek(fd, off, os.SEEK_SET)
                data = os.read(fd, length)
                self.send_json({'id': rid, 'ok': True, 'n': len(data)})
                if data:
                    self.send_raw(data)
            except OSError as e:
                self.err(rid, e)
        elif op == 'close':
            fd = self.handles.pop(req.get('h'), None)
            if fd is not None:
                try:
                    os.close(fd)
                except OSError:
                    pass
            self.send_json({'id': rid, 'ok': True})
        else:
            self.err(rid, 'unknown op: %r' % (op,))

    def serve(self):
        try:
            while True:
                line = self.readline()
                if not line.strip():
                    continue
                try:
                    req = json.loads(line.decode('utf-8'))
                except ValueError:
                    self.err(0, 'bad json')
                    continue
                try:
                    self.handle(req)
                except (OSError, ValueError) as e:
                    self.err(req.get('id', 0) if isinstance(req, dict) else 0, e)
        except (ConnectionError, OSError):
            pass
        finally:
            for fd in self.handles.values():
                try:
                    os.close(fd)
                except OSError:
                    pass
            self.handles.clear()


def main():
    global ROOT
    opts = parse_args(sys.argv[1:])
    ROOT = os.path.abspath(opts['root']) if opts['root'] else ''
    name = PIPE_PREFIX + opts['pipe']
    print('[fileproxy] listening on %s' % name, file=sys.stderr)

    while True:
        # 每客户端一个管道实例（循环里重建）。nMaxInstances 必须给
        # PIPE_UNLIMITED_INSTANCES：给 1 时上一个客户端未断开，
        # CreateNamedPipe 会 ERROR_PIPE_BUSY 直接把服务端崩掉。
        # 与 .NET NamedPipeServerStream / node net.Server 的多实例语义对齐。
        h = _winapi.CreateNamedPipe(
            name,
            _winapi.PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | _winapi.PIPE_WAIT,
            _winapi.PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, 0)
        try:
            _winapi.ConnectNamedPipe(h, None)
        except OSError as e:
            if e.winerror != _winapi.ERROR_PIPE_CONNECTED:   # 客户端先连上也算成功
                _winapi.CloseHandle(h)
                raise
        fd = msvcrt.open_osfhandle(h, os.O_RDWR | os.O_BINARY)
        conn = Conn(open(fd, 'rb+', buffering=0, closefd=True))
        t = threading.Thread(target=conn.serve, daemon=True)
        t.start()
        if opts['once']:
            t.join()
            return


if __name__ == '__main__':
    main()
