#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""gen_test_log.py — 生成大规模 logcat 测试日志（默认 1 GiB）。

面向本项目的 LogParser / LogDocument / CrashExtractor / FilterEngine 压测：
  格式: threadtime / ymd threadtime / time / brief / tag / long（含年份前缀、usec/nsec 小数秒、右对齐 tag 填充）
  级别: V D I W E F A（按真实频次分布，I 最多、F/A 稀少）
  特殊: 分段横幅（beginning of main/system/crash/events、timezone）、缩进续行、
        空消息行、行尾空格、超长行（~8KB）、UTF-8 中文与 emoji、tag 含点/方括号/中文
  崩溃: Java FATAL EXCEPTION（含 Caused by / Process 行）、Native signal、
        tombstone（*** *** 头）、ANR in <pkg>、以及 3 类不误报干扰样本
        （D 级文本含 FATAL EXCEPTION 字样、聊天文本含 signal N (SIGXXX)、非 ANR in 前缀）

用法:
  python scripts/gen_test_log.py [--out testdata/big_test.log] [--size 1G]

输出为 LF 换行、UTF-8 编码（与 adb logcat > file 一致）。固定随机种子，可复现。
"""

import argparse
import random
import sys
import time

# ── 基本常量 ──────────────────────────────────────────────────────────────────

DAY_MS = 86_400_000
START_DAY = "10-08"           # 起始日期（mm-dd），时间从 00:00:00.000 开始
YEAR = 2026                   # ymd 格式行与 long 年份前缀使用
NL_DAYS = 10                  # 预备天数（mod 使用，防止越界）

# 进程池: (pid, 进程名) —— chatty / Process: 行与之保持一致
PROCS = [
    (1234, "system_server"), (1080, "com.android.systemui"),
    (2210, "com.android.phone"), (2388, "com.android.bluetooth"),
    (3155, "com.example.shop"), (3210, "com.example.reader"),
    (4102, "com.example.game"), (4300, "com.example.camera"),
    (5231, "com.example.im"), (6620, "com.example.map"),
    (7899, "com.example.music"), (8123, "com.android.chrome"),
    (9034, "com.example.pay"), (9527, "com.example.push"),
    (10488, "com.example.work"), (12033, "com.example.video"),
    (15001, "com.example.reader:push"), (18044, "com.example.game:gl"),
    (20311, "com.example.shop:cart"), (24088, "com.example.nativeapp"),
    (29001, "com.example.mixed"),
]

# tag 池（高频段 + 长尾段）
TAGS_HOT = [
    "ActivityManager", "ActivityTaskManager", "WindowManager", "InputDispatcher",
    "InputMethodService", "SurfaceFlinger", "Choreographer", "ViewRootImpl",
    "OpenGLRenderer", "AudioTrack", "AudioFlinger", "MediaCodec", "CameraService",
    "chatty", "libc",
]
TAGS_LONG = [
    "BluetoothAdapter", "bt_stack", "WifiHal", "WifiService", "ConnectivityManager",
    "NetworkMonitor", "Netd", "DNS resolver", "Ethernet", "Tethering",
    "UsbDeviceManager", "MtpServer", "RILJ", "RILC", "GsmCdmaPhone",
    "ServiceState", "telephony", "SensorManager", "sensors_hidl", "LocationManager",
    "GnssLocationProvider", "PowerManagerService", "BatteryService",
    "BatteryStatsService", "NotificationService", "NotificationManager",
    "JobScheduler", "AlarmManager", "DownloadManager", "PackageManager",
    "PackageInstaller", "installd", "vold", "logd", "Zygote",
    "art", "ART", "System", "System.err", "System.out", "AndroidRuntime",
    "StrictMode", "AccessibilityManager", "ClipboardService", "ImeTracker",
    "InsetsController", "DisplayManagerService", "Vulkan", "EGL_emulation",
    "Gralloc", "HWC2", "HardwareRenderer", "RecyclerView", "Glide", "okhttp",
    "Retrofit", "FirebaseCrash", "Crashlytics", "Unity", "il2cpp", "DEBUG",
    "crash_dump64", "tombstoned", "SELinux", "auditd", "kernel", "adb",
    "BackupManagerService", "StorageManager", "MediaProvider", "DocumentsUI",
    "FileProvider", "WebView", "chromium", "CronetEngine", "NativeCrypto",
    "Conscrypt", "Lottie", "ExoPlayerImpl", "VideoCapabilities", "OMXClient",
    "CCodec", "AudioPolicyService", "VibratorService", "LightsService",
    "SensorService", "ThermalService", "statsd", "appops", "DeviceIdleController",
    "WallpaperService", "TextServicesManager", "SoftInput", "IME",
    "ViewRootImpl[MainActivity]", "MainActivity", "CartFragment", "GLThread",
    "pool-1-thread-2", "NativeCrash", "WeChat", "MiPush", "TencentXG",
    "讯飞语音", "高德地图", "地图引擎", "推送服务", "语音引擎", "OCR识别",
]
SPECIAL_TAGS = ["A.B.C", "ViewRootImpl[Settings]", "com.example.Media", "Tag.tag-1_x"]

# 消息模板池（{} 由随机数字/词填充，保证短小）
MSG_V = ["tick {}", "frame {} drawn", "idle poll #{}", "state -> s{}", "seq {} ack",
         "probe try {}", "cache hit r{}", "vsync {}", "poll fd {}", "step {}",
         "dbg {}", "iter {} done", "q {} empty", "hint {}", "noop {}", "pass {}",
         "wake lock held {}ms", "attempt {}", "flag {}", "uid {}", "count {}",
         "slot {} free", "epoch {}", "wait {}ms"]
MSG_D = ["onClick x={} y={}", "query {} rows in {}ms", "notify item {} changed",
         "resume frag #{}", "start activity {}", "bind view {}", "prefetch page {}",
         "skip frame {}", "dispatch event {}", "read {} bytes", "parse ok id={}",
         "set prop {}={}", "load lib {}", "task {} queued", "uri path {}",
         "anim start d={}", "check pt {}", "resize {}x{}", "scroll y={}",
         "intent act #{}", "key guard {}", "drag d{}", "buffer size {}",
         "flush seq {}", "reload page {}", "jit warm {}", "deopt method #{}"]
MSG_I = ["App started in {}ms", "onResume {}", "GC freed {} objects in {}ms",
         "Displayed com.example/.ui.MainActivity +{}ms", "Found {} update(s)",
         "heartbeat ok seq={}", "connected to {}", "service bound #{}",
         "install done pkg-{}", "update applied v{}", "scan found {} networks",
         "sync batch {} ok", "job finished id={}", "alarm fired tag #{}",
         "battery level {}%", "power save {}", "buffer fill {}%",
         "frame stats: {} dropped", "activity paused {}", "report sent size={}",
         "captured {} frames", "cached {} entries", "loaded {}ms",
         "session id {}", "token refreshed exp={}", "push received id={}",
         "notification shown #{}", "screenshot saved {}KB", "录制 {} 段完成",
         "加载完成，用时 {}ms", "同步 {} 条记录", "签到成功 seq={}",
         "内存充裕：可用 {}MB", "云端配置拉取 v{} 成功"]
MSG_W = ["Slow operation {}ms on main", "Nearby scan throttled",
         "Retry {}/{} for req-{}", "Disk low: {}MB free", "Timeout waiting {}ms",
         "Deprecated api {}", "Leaked {} IntentReceiver", "Skipped {} frames! "
         "The application may be doing too much work on its main thread.",
         "No adapter attached; skipping layout", "uri resolver slow {}ms",
         "weak ref cleared x{}", "signal strength poor {}", "backpressure: {}",
         "cache evicted {} entries", "battery saver on, defer #{}",
         "memory trim level {}", "ANR watchdog tick {} (light)"]
MSG_E = ["Failed to load url rc={}", "SocketTimeoutException: connect {}ms",
         "SQL error code {}", "handshake failed err={}", "decode fail frame {}",
         "write failed: broken pipe (errno {})", "permission denied: {}",
         "callback missing req-{}", "parse error at col {}", "io error on {}",
         "canvas: trying to use a recycled bitmap", "获取配置失败 code={}"]
MSG_F = ["Abort: bad state {}", "Fatal: stream closed mid-frame {}", "abort() called"]
MSG_A = ["ASSERT check failed: {}", "AUDIT: seam violation #{}"]
MSG_ZH = ["网络切换：{}", "⚠️ 电量低于 {}%", "语音识别结果：{}", "地图标注加载 {} 个",
          "推送到达 📨 id={}", "支付回调成功 ✅", "埋点上报 {} 条"]
MSG_WORDS = ("the quick brown fox jumps over lazy dog logcat buffer overflow "
             "test data message padding fill line long content words a b c 0 1 2").split()

EXC_TYPES = ["java.lang.NullPointerException", "java.lang.IllegalStateException",
             "java.io.IOException", "java.lang.IndexOutOfBoundsException",
             "java.lang.ClassCastException", "java.lang.OutOfMemoryError",
             "java.lang.ArrayIndexOutOfBoundsException",
             "android.database.CursorIndexOutOfBoundsException"]
JAVA_METHODS = ["com.example.shop.ui.CartFragment.bindCart",
                "com.example.reader.page.PagerView.onDraw",
                "com.example.im.chat.ChatAdapter.onBind",
                "com.example.map.overlay.MarkerLayer.update",
                "com.example.game.scene.Renderer.onFrame"]
SIGS = [(11, "SIGSEGV", ["1 (SEGV_MAPERR)", "2 (SEGV_ACCERR)"]),
        (6, "SIGABRT", ["0 (SI_TKILL)"]),
        (7, "SIGBUS", ["1 (BUS_ADRERR)"]),
        (4, "SIGILL", ["1 (ILL_ILLOPC)"]),
        (8, "SIGFPE", ["1 (FPE_INTDIV)"])]
NATIVE_LIBS = ["/apex/com.android.runtime/lib64/bionic/libc.so (strlen+{}x)",
               "/data/app/~~abc123/com.example.nativeapp/libnative.so (render_frame+{}x)",
               "/system/lib64/libhwui.so (android::Canvas::draw+{}x)",
               "/vendor/lib64/mtk-camera.so (cam_capture+{}x)"]
FINGERPRINT = ("Build fingerprint: 'google/panther/panther:14/"
               "AP2A.240905.003/2024090500:user/release-keys'")

# ── 工具 ─────────────────────────────────────────────────────────────────────


def parse_size(s: str) -> int:
    s = s.strip().upper()
    for suf, mult in (("KIB", 1024), ("MIB", 1024 ** 2), ("GIB", 1024 ** 3),
                      ("K", 1024), ("M", 1024 ** 2), ("G", 1024 ** 3)):
        if s.endswith(suf):
            return int(s[:-len(suf)]) * mult
    return int(s)


def fmt_mmdd(t: int, rng) -> str:
    day = (t // DAY_MS) % NL_DAYS
    d = 8 + day
    return "10-%02d" % d if d <= 31 else "11-%02d" % (d - 31)


def crash_group(kind, rng, pid, tproc):
    """构造一组崩溃/干扰行（不含时间戳头的行由调用方包上时间戳？不：每行独立成头）。"""
    # 返回 [(level, tag, msg), ...]，调用方用同一时间窗格式化为 threadtime
    g = []
    pkg = tproc[1]
    if kind == "java":
        exc = rng.choice(EXC_TYPES)
        g.append(("E", "AndroidRuntime", "FATAL EXCEPTION: main"))
        g.append(("E", "AndroidRuntime", "Process: %s, PID: %d" % (pkg, pid)))
        g.append(("E", "AndroidRuntime", "%s: attempt to access field on null object" % exc))
        for i in range(rng.randint(4, 8)):
            g.append(("E", "AndroidRuntime",
                      "    at %s(%s.java:%d)" % (rng.choice(JAVA_METHODS),
                                                 pkg.split(".")[-1], rng.randint(20, 900))))
        g.append(("E", "AndroidRuntime",
                  "Caused by: java.io.IOException: offline cache corrupted"))
        g.append(("E", "AndroidRuntime", "    ... %d more" % rng.randint(3, 30)))
        if rng.random() < 0.3:
            g.append(("W", pkg, "uncaught exception reported, dying..."))
    elif kind == "native":
        sig, name, codes = rng.choice(SIGS)
        tid = pid + rng.randint(1, 999)
        g.append(("F", "libc", "Fatal signal %d (%s), code %s, fault addr 0x%08x "
                  "in tid %d (RenderThread), pid %d" %
                  (sig, name, rng.choice(codes), rng.randint(1, 0xFFFFFFF), tid, pid)))
        g.append(("I", "crash_dump64", "obtaining output fd from tombstoned, type: kDebuggerdTombstone"))
        g.append(("F", "DEBUG", "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***"))
        g.append(("F", "DEBUG", FINGERPRINT))
        g.append(("F", "DEBUG", "signal %d (%s), code %s, fault addr 0x%08x" %
                  (sig, name, rng.choice(codes), rng.randint(1, 0xFFFF))))
        g.append(("F", "DEBUG", "Cause: null pointer dereference"))
        g.append(("F", "DEBUG", "backtrace:"))
        for i in range(rng.randint(3, 6)):
            g.append(("F", "DEBUG", "      #0%d pc %016x  %s" %
                      (i, rng.randint(0x1000, 0xFFFFFFF),
                       rng.choice(NATIVE_LIBS).replace("{}", str(rng.randint(1, 0x2000))))))
        if rng.random() < 0.4:
            g.append(("F", "DEBUG", "      #0%d pc %016x  %s" %
                      (rng.randint(6, 9), rng.randint(0x1000, 0xFFFFFFF),
                       rng.choice(NATIVE_LIBS).replace("{}", str(rng.randint(1, 0x2000))))))
    elif kind == "tombstone-noise":
        # 有 *** *** 头但 signal 不在 FATAL 行内（另一行给出），归 tombstone
        g.append(("F", "DEBUG", "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***"))
        g.append(("F", "DEBUG", FINGERPRINT))
        g.append(("F", "DEBUG", "signal 6 (SIGABRT), code -1 (SI_QUEUE), fault addr --"))
        g.append(("F", "DEBUG", "    r0 00000000  r1 00000001  r2 00000000"))
        g.append(("F", "DEBUG", "backtrace:"))
        g.append(("F", "DEBUG", "      #00 pc %016x  %s" %
                  (rng.randint(0x1000, 0xFFFFFF),
                   rng.choice(NATIVE_LIBS).replace("{}", str(rng.randint(1, 0x2000))))))
    elif kind == "anr":
        g.append(("E", "ActivityManager", "ANR in %s/.ui.MainActivity" % pkg))
        g.append(("E", "ActivityManager",
                  "Reason: Input dispatching timed out (com.example/%s, Waiting "
                  "to send key event because the touched window has not finished "
                  "processing all of the input events)" % pkg.replace(".", "/")))
        g.append(("E", "ActivityManager", "Load: %.1f / %.1f / %.1f" %
                  (rng.uniform(0.5, 9.9), rng.uniform(0.5, 9.9), rng.uniform(0.5, 9.9))))
        g.append(("E", "ActivityManager", "CPU usage from %dms to %dms later" %
                  (rng.randint(-5000, -100), rng.randint(50, 8000))))
        for i in range(rng.randint(2, 4)):
            g.append(("E", "ActivityManager", "  %d%% %s: %d%% user + %d%% kernel" %
                      (rng.randint(1, 60), pkg, rng.randint(1, 30), rng.randint(1, 20))))
    elif kind == "noise":
        n = rng.random()
        if n < 0.34:
            # D 级业务日志带 FATAL EXCEPTION 字样、无 Process 行 → 不应误报
            g.append(("D", rng.choice(TAGS_LONG),
                      'user typed "FATAL EXCEPTION" in chat input box'))
        elif n < 0.67:
            # 聊天文本含 signal N (SIGXXX) → 不应误报
            g.append(("I", "ChatService",
                      "forwarded message: https://exa.mp/link?note=signal %d (%s) "
                      "check this out" % (rng.choice([11, 6, 9]), "SIGSEGV")))
        else:
            # 非 "ANR in <pkg>" 前缀的 ANR 字样 → 不应误报
            g.append(("W", rng.choice(TAGS_LONG), "ANR got resolved by user input, window restored"))
    return g


def main():
    ap = argparse.ArgumentParser(description="生成大规模 logcat 测试日志")
    ap.add_argument("--out", default="testdata/big_test.log")
    ap.add_argument("--size", default="1G", help="目标大小，如 100M / 512M / 1G / 1GiB")
    args = ap.parse_args()
    target = parse_size(args.size)

    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    rng = random.Random(20261011)
    rc, ri, rf = rng.choice, rng.randint, rng.random

    # 预生成时间字符串表（性能关键）
    HMS = ["%02d:%02d:%02d" % (h, m, s) for h in range(24) for m in range(60) for s in range(60)]
    MS3 = [".%03d" % i for i in range(1000)]

    t = 0                     # 起始日 00:00:00.000 起的毫秒
    step_seq = ri(8, 30)      # 平均步长基准
    step_left = 0

    n = 0                     # 行号（0 起）
    size = 0
    fmt_cnt = {}
    lvl_cnt = {}
    crash_cnt = {}
    next_crash = ri(30_000, 50_000)
    next_special = ri(60_000, 90_000)   # 长行/空行等杂项排程
    next_banner = ri(1_500_000, 2_500_000)

    procs = PROCS
    n_procs = len(procs)

    import os
    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    CH = 20_000
    fh = open(args.out, "wb")
    t0 = time.time()
    last_mb = 0

    def new_step():
        # 平常 8~30ms，小概率跳 0.5~90s（模拟静默与突发）
        if rf() < 0.01:
            return ri(500, 90_000)
        return ri(8, 30)

    def ts_line(day_str, hms, ms3):
        return day_str + " " + hms + ms3

    def emit(level, tag, msg):
        return level + " " + tag + ": " + msg

    while size < target:
        batch = []
        ap_ = batch.append
        for _ in range(CH):
            n += 1
            step_left -= 1
            if step_left <= 0:
                step_seq = new_step()
                step_left = ri(1, 4)
            t += step_seq
            day_ms = t % DAY_MS
            day_str = fmt_mmdd(t, rng)
            hms = HMS[day_ms // 1000]
            ms3 = MS3[day_ms % 1000]

            pi = ri(0, n_procs - 1)
            pid, pname = procs[pi]
            tid = pid + ri(1, 999)

            # ── 排程事件 ──
            if n >= next_banner:
                ap_("%s %s %s --------- beginning of %s\n" %
                    (day_str, hms, MS3[day_ms % 1000][1:],
                     rc(("main", "system", "crash", "events", "kernel"))))
                if rf() < 0.25:
                    ap_("%s %s %s ----- timezone:Asia/Shanghai\n"
                        % (day_str, hms, MS3[day_ms % 1000][1:]))
                next_banner = n + ri(1_500_000, 2_500_000)
                fmt_cnt["banner"] = fmt_cnt.get("banner", 0) + 1
                continue

            if n >= next_crash:
                r = rf()
                kind = ("java" if r < 0.30 else "native" if r < 0.55
                        else "anr" if r < 0.72 else "tombstone-noise" if r < 0.80
                        else "noise")
                g = crash_group(kind, rng, pid, (pid, pname))
                # 组内时间戳取同一时刻（真实崩溃组都在极短时间窗内，且天然覆盖
                # 「同一时间戳多行」的解析路径）；接近天末时跳过以免跨天溢出。
                if day_ms < DAY_MS - 5_000:
                    for lv, tg, msg in g:
                        ap_("%s %s%s %d %d %s %s: %s\n"
                            % (day_str, hms, ms3, pid, tid, lv, tg, msg))
                        lvl_cnt[lv] = lvl_cnt.get(lv, 0) + 1
                next_crash = n + len(g) + ri(30_000, 50_000)
                n += len(g)
                crash_cnt[kind] = crash_cnt.get(kind, 0) + 1
                fmt_cnt["crash:%s" % kind] = fmt_cnt.get("crash:%s" % kind, 0) + 1
                continue

            if n >= next_special:
                r = rf()
                if r < 0.45:
                    # 超长行 ~8KB
                    words = " ".join(rc(MSG_WORDS) for _ in range(1100))
                    ap_("%s %s %s D %s: LONGDUMP %s\n" % (day_str, hms, ms3[1:], rc(TAGS_LONG), words))
                    fmt_cnt["longline"] = fmt_cnt.get("longline", 0) + 1
                elif r < 0.70:
                    # 空行 / 空消息 / 行尾空格
                    k = rf()
                    if k < 0.33:
                        ap_("\n")
                    elif k < 0.66:
                        ap_("%s %s %s I %s:\n" % (day_str, hms, ms3[1:], rc(TAGS_LONG)))
                    else:
                        ap_("%s %s %s D %s: trailing spaces   \n"
                            % (day_str, hms, ms3[1:], rc(TAGS_LONG)))
                    fmt_cnt["emptyish"] = fmt_cnt.get("emptyish", 0) + 1
                elif r < 0.85:
                    # 缩进续行组（2~6 行，不带头）
                    m0 = rc(MSG_I) if rf() < 0.5 else "dump begin:"
                    ap_("%s %s %s I %s: %s\n" % (day_str, hms, ms3[1:], rc(TAGS_LONG), m0))
                    for _j in range(ri(2, 6)):
                        ap_("    state=%d items=%d ptr=0x%08x\n"
                            % (ri(0, 9), ri(0, 64), ri(1, 0xFFFFFFF)))
                    n += ri(2, 6)
                    fmt_cnt["continuation"] = fmt_cnt.get("continuation", 0) + 1
                else:
                    # chatty 折叠
                    ap_("%s %s %s I chatty: uid=10%03d %s expire %d lines\n"
                        % (day_str, hms, ms3[1:], ri(0, 999), pname.replace("com.", ""),
                           ri(2, 60)))
                    fmt_cnt["chatty"] = fmt_cnt.get("chatty", 0) + 1
                next_special = n + ri(60_000, 90_000)
                continue

            # ── 常规行（按格式概率分布）──
            r = rf()
            if r < 0.80 or 0.92 <= r < 0.99:
                # threadtime: mm-dd hh:mm:ss.mmm pid tid L Tag: msg
                lvl = rc("IIIIIIIIIIIIIIIIIIIIDDDDDDDDDDWWWWVVVVEEEEEFFA")
                tag = rc(TAGS_HOT) if rf() < 0.35 else (rc(TAGS_LONG) if rf() < 0.85 else rc(SPECIAL_TAGS))
                pool = MSG_V if lvl == "V" else MSG_D if lvl == "D" else MSG_I if lvl == "I" \
                    else MSG_W if lvl == "W" else MSG_E if lvl == "E" else MSG_F if lvl == "F" else MSG_A
                if rf() < 0.04:
                    msg = rc(MSG_ZH).replace("{}", str(ri(1, 9999)))
                else:
                    msg = rc(pool).replace("{}", str(ri(1, 9999)))
                ap_("%s %s%s %d %d %s %s: %s\n" % (day_str, hms, ms3, pid, tid, lvl, tag, msg))
                fmt_cnt["threadtime"] = fmt_cnt.get("threadtime", 0) + 1
            elif r < 0.815:
                # usec / nsec 小数秒（基于真实亚毫秒扩展，保证时间单调不倒退）
                lvl = rc("IDWE")
                tag = rc(TAGS_HOT + TAGS_LONG)
                sub = t % 1000
                frac = ("%06d" % (sub * 1000 + ri(0, 999))) if rf() < 0.5 \
                    else ("%09d" % (sub * 1_000_000 + ri(0, 999_999)))
                ap_("%s %s.%s %d %d %s %s: %s\n"
                    % (day_str, hms, frac, pid, tid, lvl, tag,
                       rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["usec-nsec"] = fmt_cnt.get("usec-nsec", 0) + 1
            elif r < 0.83:
                # ymd threadtime: yyyy-mm-dd hh:mm:ss.mmm pid tid L Tag: msg
                lvl = rc("IIWDE")
                tag = rc(TAGS_LONG)
                ap_("%d-%s %s%s %d %d %s %s: %s\n"
                    % (YEAR, day_str, hms, ms3, pid, tid, lvl, tag,
                       rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["ymd"] = fmt_cnt.get("ymd", 0) + 1
            elif r < 0.85:
                # time 格式: L/Tag(pid): msg（pid 右对齐可能带填充）
                lvl = rc("IDWE")
                tag = rc(TAGS_HOT + TAGS_LONG)
                pad = " " * ri(0, 3)
                ap_("%s %s%s %s/%s(%s%d): %s\n"
                    % (day_str, hms, ms3, lvl, tag, pad, pid,
                       rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["time"] = fmt_cnt.get("time", 0) + 1
            elif r < 0.862:
                # brief: L/Tag(pid): msg（无时间）
                lvl = rc("IDW")
                ap_("%s/%s(%d): %s\n" % (lvl, rc(TAGS_LONG), pid,
                                         rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["brief"] = fmt_cnt.get("brief", 0) + 1
            elif r < 0.872:
                # tag: L/Tag: msg（无时间无 pid）
                lvl = rc("IDW")
                ap_("%s/%s: %s\n" % (lvl, rc(TAGS_LONG),
                                     rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["tagfmt"] = fmt_cnt.get("tagfmt", 0) + 1
            elif r < 0.905:
                # long 块: [ mm-dd hh:mm:ss.mmm pid: tid L/Tag ] + msg 行
                lvl = rc("IDWE")
                tag = rc(TAGS_HOT + TAGS_LONG)
                blk = ri(2, 5)
                ap_("[ %s %s%s  %d: %d %s/%s ]\n" % (day_str, hms, ms3, pid, tid, lvl, tag))
                for _j in range(blk):
                    ap_(rc(MSG_D).replace("{}", str(ri(1, 999))) + "\n")
                n += blk
                fmt_cnt["long"] = fmt_cnt.get("long", 0) + 1
            elif r < 0.92:
                # 右对齐 tag 填充: Tag    : msg
                lvl = rc("IDWE")
                tag = rc(TAGS_HOT)
                ap_("%s %s%s %d %d %s %-8s: %s\n"
                    % (day_str, hms, ms3, pid, tid, lvl, tag,
                       rc(MSG_D).replace("{}", str(ri(1, 999)))))
                fmt_cnt["padtag"] = fmt_cnt.get("padtag", 0) + 1
            else:
                # 裸正文行（无任何头，作为普通行兜底，少量）
                ap_(rc(MSG_WORDS) + " " + rc(MSG_WORDS) + "\n")
                fmt_cnt["bare"] = fmt_cnt.get("bare", 0) + 1

        data = "".join(batch).encode("utf-8")
        fh.write(data)
        size += len(data)
        mb = size // (1024 * 1024)
        if mb - last_mb >= 128:
            last_mb = mb
            el = time.time() - t0
            sys.stdout.write("  %4d MiB / %d MiB   %d lines   %.0f s\n"
                             % (mb, target // (1024 * 1024), n, el))
            sys.stdout.flush()

    fh.close()
    el = time.time() - t0
    print("done: %s" % args.out)
    print("  size   : %d bytes (%.2f GiB)" % (size, size / 1024 ** 3))
    print("  lines  : %d" % n)
    print("  elapsed: %.1f s" % el)
    print("  fmt    : " + ", ".join("%s=%d" % kv for kv in sorted(fmt_cnt.items())))
    print("  crash  : " + ", ".join("%s=%d" % kv for kv in sorted(crash_cnt.items())))


if __name__ == "__main__":
    main()
