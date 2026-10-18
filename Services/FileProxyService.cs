using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace logcat.Services;

/// <summary>
/// 文件代理编排：连接/自动拉起、spool 全流程、超大文件预警、暂存路径规则、使用判定。
/// 协议细节在 <see cref="FileProxyClient"/>；本类只做「拿到字节流之后」的事。
/// </summary>
public static class FileProxyService
{
    /// <summary>spool 块大小（8MB，顺序写）。</summary>
    public const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>试连手动代理的快失败超时（200ms）。</summary>
    public const int TryConnectMs = 200;

    /// <summary>拉起后轮询管道就绪的间隔（200ms）。</summary>
    public const int PollMs = 200;

    /// <summary>超大文件预警阈值（1GB，写死不进设置——spool 相关均不加设置字段，决策 D14）。</summary>
    public const long HugeFileThreshold = 1L * 1024 * 1024 * 1024;

    /// <summary>管道名长度上限（Windows 命名管道名上限 256）。</summary>
    public const int PipeNameMaxLen = 256;

    /// <summary>
    /// 校验管道名：非空、不含路径分隔符（\ 与 /，命名管道名两者皆非法）、不超长。返回错误消息的资源键；合法返回 null。
    /// 对话框保存与启动命令构造共用同一判据（spec「启动命令构造与管道名校验」）。
    /// </summary>
    public static string? ValidatePipeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "管道名不能为空";
        if (name.Contains('\\') || name.Contains('/')) return "管道名不能包含路径分隔符";
        if (name.Length > PipeNameMaxLen) return "管道名过长（上限 {0} 个字符）";
        return null;
    }

    // ── 启动命令构造 ──

    /// <summary>
    /// 拼出完整命令行（展示 / 预览 / 排障用）：可执行文件 + 组装后的参数（<see cref="BuildLaunchArgs"/>：
    /// {pipe} 占位符替换或末尾自动追加 --pipe），含空格处自动加引号。
    /// </summary>
    public static string BuildCommandLine(string exe, string argsTemplate, string pipeName)
    {
        var args = BuildLaunchArgs(argsTemplate, pipeName);
        return QuoteIfNeeded(exe) + (args.Length > 0 ? " " + args : "");
    }

    /// <summary>
    /// internal：组装实际传给代理进程的参数（design D17）。
    /// 旧形态：模板含 {pipe} 占位符 → 仍按替换处理（兼容已存 settings.json 与手改配置）；
    /// 新形态：模板不含 {pipe} → 参数末尾自动追加 `--pipe 管道名`（管道名单一来源，参数栏只填其余参数）。
    /// 启动进程与命令行预览/留痕共用此串，避免两处口径漂移。
    /// </summary>
    internal static string BuildLaunchArgs(string? argsTemplate, string pipeName)
        => (argsTemplate ?? "").Contains("{pipe}", StringComparison.Ordinal)
            ? SubstPipe(argsTemplate, pipeName)
            : AppendPipeArg((argsTemplate ?? "").Trim(), pipeName);

    /// <summary>internal：旧 settings 的参数模板可能带尾部 `--pipe {pipe}`，载入展示时剥掉（服务层会自动追加）。</summary>
    internal static string NormalizeLegacyArgs(string? args)
    {
        string s = (args ?? "").Trim();
        if (s.EndsWith("--pipe {pipe}", StringComparison.OrdinalIgnoreCase))
            s = s[..^"--pipe {pipe}".Length].TrimEnd();
        else if (s.EndsWith("--pipe={pipe}", StringComparison.OrdinalIgnoreCase))
            s = s[..^"--pipe={pipe}".Length].TrimEnd();
        return s;
    }

    static string AppendPipeArg(string args, string pipeName)
        => args.Length > 0 ? args + " --pipe " + QuoteIfNeeded(pipeName) : "--pipe " + QuoteIfNeeded(pipeName);

    /// <summary>internal：只替换 {pipe} 的参数部分（ProcessStartInfo.Arguments 用同一串，避免两处口径漂移）。</summary>
    internal static string SubstPipe(string? argsTemplate, string pipeName)
        => (argsTemplate ?? "").Replace("{pipe}", QuoteIfNeeded(pipeName), StringComparison.Ordinal);

    static string QuoteIfNeeded(string s)
        => s.Length > 0 && (s.Contains(' ') || s.Contains('\t')) ? "\"" + s + "\"" : s;

    // ── 连接与自动拉起 ──

    /// <summary>
    /// 确保连上代理：先试连手动代理（<see cref="TryConnectMs"/> 快失败）→ 连不上且配了启动命令
    /// 才拉起 → 每 <see cref="PollMs"/> 轮询至超时。未配启动命令不启动任何进程（spec「代理拉起条件与超时」）。
    /// </summary>
    public static async Task<FileProxyClient> EnsureProxyAsync(FileProxySettings settings, CancellationToken ct = default)
    {
        // 先试连手动代理：连上就用，与是否配置了启动命令无关
        try
        {
            return await FileProxyClient.ConnectAsync(settings.PipeName, TryConnectMs, ct).ConfigureAwait(false);
        }
        catch (FileProxyException ex) when (ex.Code == FileProxyErrorCode.NoProxyNotRunning)
        {
            // 没连上：留痕（管道名 + 错误）后进入拉起分支
            StartupLog.Write($"[FileProxy] 手动代理连接失败：{settings.PipeName} — {ex.Message}");
        }
        // proto 不匹配等其它错误直接抛出——有东西在监听，但它不是预期的代理，不尝试覆盖

        if (string.IsNullOrWhiteSpace(settings.LaunchFile))
            throw new FileProxyException(FileProxyErrorCode.NoProxyNotRunning,
                Loc.T("未连接到文件代理。请确认代理正在运行，或在「设置 - 文件代理」中配置启动命令。"));

        var pipeErr = ValidatePipeName(settings.PipeName);
        if (pipeErr != null)
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理管道名无效：{0}", pipeErr.Contains("{0}", StringComparison.Ordinal)
                    ? Loc.F(pipeErr, PipeNameMaxLen) : Loc.T(pipeErr)));

        string cmdLine = BuildCommandLine(settings.LaunchFile, settings.LaunchArgs, settings.PipeName);
        StartupLog.Write($"[FileProxy] 启动代理：{cmdLine}");

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(settings.LaunchFile, BuildLaunchArgs(settings.LaunchArgs, settings.PipeName))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[FileProxy] 启动失败：{ex.Message}");
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理启动失败：{0}（命令行：{1}）", ex.Message, cmdLine), ex);
        }
        if (proc != null)
        {
            RegisterLaunched(proc);
            // 留痕：完整命令行 + PID（spec「StartupLog 留痕」；进程可能秒退，Id 取不到就留 0）
            int pid = 0;
            try { pid = proc.Id; } catch { /* 已退出 */ }
            StartupLog.Write($"[FileProxy] 启动代理：{cmdLine}（PID {pid}）");
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var client = await FileProxyClient.ConnectAsync(settings.PipeName, TryConnectMs, ct).ConfigureAwait(false);
                StartupLog.Write($"[FileProxy] 代理已就绪：{client.ProxyName}（拉起耗时 {sw.ElapsedMilliseconds} ms）");
                return client;
            }
            catch (FileProxyException ex) when (ex.Code == FileProxyErrorCode.NoProxyNotRunning)
            {
                // 还没就绪：继续轮询
            }
            if (sw.ElapsedMilliseconds >= settings.TimeoutMs) break;
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }

        throw new FileProxyException(FileProxyErrorCode.LaunchTimeout,
            Loc.F("文件代理启动超时（{0} ms）。命令行：{1}", settings.TimeoutMs, cmdLine))
        { CommandLine = cmdLine };
    }

    // ── spool 全流程 ──

    /// <summary>
    /// spool 进度派生纯函数（决策 D12）：（实收字节数, open.size）→ (进度 0–0.5, 文案)。
    ///
    /// 正常态：进度随实收线性增长到 50%（剩下 50% 留给索引构建）。
    /// 追逐态（实收已超 open.size——源文件在拉取间隙继续增长，百分比分母失效）：
    /// 进度驻留 50%，文案切「已接收 X MB · 源文件持续增长」并随块刷新，
    /// 禁止静态文案 + 静止进度条组合（spec「追逐态可观测」）。
    /// </summary>
    public static (double Pct, string Msg) SpoolProgress(long received, long expected)
    {
        if (expected > 0 && received <= expected)
        {
            var pct = Math.Min(0.5, 0.5 * received / expected);
            return (pct, Loc.F("正在经文件代理读取… {0}%", (int)(pct * 100)));
        }
        return (0.5, Loc.F("正在经文件代理读取（已接收 {0} MB，源文件持续增长）…", received / (1024 * 1024)));
    }

    /// <summary>
    /// 经代理拉取并 spool 落盘（一体化入口）：open → 逐块写 → close → 完整性判定。
    /// 超大文件确认框插在 open 与写盘之间的场景用 <see cref="OpenSpoolAsync"/> + <see cref="SpoolCoreAsync"/>。
    /// </summary>
    /// <param name="sessionDir">本次暂存目录（guid 子目录，调用方传入以便取消/失败时删）。</param>
    public static async Task<SpoolResult> SpoolAsync(FileProxyClient client, string originalPath, string sessionDir,
        IProgress<(double Pct, string Msg)>? progress = null, CancellationToken ct = default)
    {
        var (handle, size) = await client.OpenAsync(originalPath, ct).ConfigureAwait(false);
        return await SpoolCoreAsync(client, handle, size, originalPath, sessionDir, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// spool 前半：连接代理（自动拉起）并 open，返回可确认超大文件再决定是否写盘的会话。
    /// 调用方拿到 <see cref="SpoolSession"/> 后先做阈值确认（UI 交互），再进 <see cref="SpoolCoreAsync"/>。
    /// </summary>
    public static async Task<SpoolSession> OpenSpoolAsync(string originalPath, string sessionDir, CancellationToken ct = default)
    {
        var client = await EnsureProxyAsync(FileProxySettings.FromAppSettings(), ct).ConfigureAwait(false);
        try
        {
            var (handle, size) = await client.OpenAsync(originalPath, ct).ConfigureAwait(false);
            return new SpoolSession(client, handle, size, originalPath, sessionDir,
                Path.Combine(sessionDir, SpoolPaths.SpoolFileNameFor(originalPath)));
        }
        catch
        {
            ((IDisposable)client).Dispose();
            throw;
        }
    }

    /// <summary>
    /// spool 后半（写入）：8MB 块顺序读到 EOF → close → 完整性以「实收 == open.size」判定。
    /// 不符（中途断线 / 文件变长变短）删目录报 <see cref="FileProxyErrorCode.ShortRead"/>（含实收/期望）；
    /// 取消 / 抢占同样删目录。磁盘写满等 I/O 错误原文透出（无专用错误码，决策 D5/D12）。
    /// </summary>
    internal static async Task<SpoolResult> SpoolCoreAsync(FileProxyClient client, long handle, long expected,
        string originalPath, string sessionDir, IProgress<(double Pct, string Msg)>? progress = null,
        CancellationToken ct = default)
    {
        string spoolFile = Path.Combine(sessionDir, SpoolPaths.SpoolFileNameFor(originalPath));
        long received = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            StartupLog.Write($"[FileProxy] spool 开始：{originalPath} → {spoolFile}（期望 {expected} 字节）");

            Directory.CreateDirectory(sessionDir);
            using var outFs = new FileStream(spoolFile, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, useAsync: true);

            static FileProxyException ShortRead(long received, long expected) => new(FileProxyErrorCode.ShortRead,
                Loc.F("经文件代理读取不完整：实收 {0} 字节，期望 {1} 字节。可重新打开文件重试。", received, expected))
                { Received = received, Expected = expected };

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                byte[] chunk;
                try
                {
                    chunk = await client.ReadAsync(handle, received, ChunkBytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // 中途断线 / 代理异常：完整性判据不变（实收 != open.size → 短读，附实收/期望；
                    // 原始异常挂 InnerException，断线/代理错误详情不丢）。实收恰等于 open.size 时
                    // 内容已完整，缺的只是 EOF 标记，按完整收下（spec 判据即「实收 == open.size」）
                    if (received != expected)
                        throw new FileProxyException(FileProxyErrorCode.ShortRead,
                            Loc.F("经文件代理读取不完整：实收 {0} 字节，期望 {1} 字节。可重新打开文件重试。", received, expected), ex)
                        { Received = received, Expected = expected };
                    break;
                }
                if (chunk.Length == 0) break;   // n=0 即 EOF
                await outFs.WriteAsync(chunk, ct).ConfigureAwait(false);
                received += chunk.Length;
                progress?.Report(SpoolProgress(received, expected));
            }
            await client.CloseAsync(handle, ct).ConfigureAwait(false);

            if (received != expected) throw ShortRead(received, expected);

            StartupLog.Write($"[FileProxy] spool 完成：{spoolFile}（{received} 字节，{sw.ElapsedMilliseconds} ms）");
            return new SpoolResult(spoolFile, received);
        }
        catch (Exception)
        {
            // 短读 / 取消 / 断线 / I/O 错误：删本次暂存目录，无残留
            // （spec「不符即删」「取消即删」同一口径；成功路径在上方已 return，不会到这）
            DeleteDirBestEffort(sessionDir);
            throw;
        }
    }

    /// <summary>超大文件预警：open.size 超阈值需确认框阻断（取消即中止；阈值写死不进设置）。</summary>
    public static bool NeedsHugeFileConfirm(long openSize) => openSize > HugeFileThreshold;

    // ── 暂存目录删除 ──

    /// <summary>尽力删目录：删不掉只留痕，不抛（spec「取消即删」「文档关闭即删暂存目录」同一口径）。</summary>
    public static void DeleteDirBestEffort(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { StartupLog.Write($"[FileProxy] 暂存目录删除失败（留痕不抛）：{dir} — {ex.Message}"); }
    }

    // ── 启动清理（暂存目录活性判定，决策 D1/D13）──

    /// <summary>
    /// 清理暂存根目录下全部残留子目录（无时间阈值）：逐个先做跨进程活性探测，
    /// 被任何存活实例持有（含本实例自己）的跳过，无主的删除；逐目录删不掉只留痕。
    /// 安全性不依赖单实例互斥量的覆盖范围（跨会话并存由活性判定独立保证）。
    /// </summary>
    public static (int Deleted, int Skipped, int Failed) CleanupStaleSpoolDirs()
        => CleanupStaleSpoolDirs(SpoolPaths.DefaultRoot);

    /// <summary>internal：指定根目录的清理（单测用临时根，不碰应用目录）。</summary>
    internal static (int Deleted, int Skipped, int Failed) CleanupStaleSpoolDirs(string root)
    {
        int deleted = 0, skipped = 0, failed = 0;
        if (!Directory.Exists(root)) return (0, 0, 0);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (SpoolPaths.IsDirAlive(dir)) { skipped++; continue; }
            try { Directory.Delete(dir, recursive: true); deleted++; }
            catch (Exception ex)
            {
                failed++;
                StartupLog.Write($"[FileProxy] 启动清理删除失败（留痕不抛）：{dir} — {ex.Message}");
            }
        }
        StartupLog.Write($"[FileProxy] 启动清理：删 {deleted} / 跳过 {skipped} / 失败 {failed}");
        return (deleted, skipped, failed);
    }

    // ── 拉起的代理进程生命周期（决策 D10：退出双保险杀进程树）──

    static readonly List<Process> _launched = new();
    static readonly object _launchedLock = new();

    /// <summary>internal：拉起进程快照（单测验证杀进程路径用，task 6.3）。</summary>
    internal static IReadOnlyList<Process> LaunchedForTest
    {
        get { lock (_launchedLock) return _launched.ToArray(); }
    }

    static void RegisterLaunched(Process p)
    {
        lock (_launchedLock) _launched.Add(p);
    }

    /// <summary>应用退出时杀进程树：主窗关闭流程 + 进程退出事件双保险（任务 6.3）。</summary>
    public static void KillLaunchedProcesses()
    {
        lock (_launchedLock)
        {
            foreach (var p in _launched)
            {
                try
                {
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { StartupLog.Write($"[FileProxy] 杀代理进程失败（留痕不抛）：{ex.Message}"); }
                try { p.Dispose(); } catch { }
            }
            _launched.Clear();
        }
    }
}

/// <summary>文件代理的运行时配置（从 <see cref="AppSettings"/> 现读，保存即生效）。</summary>
public sealed record FileProxySettings(string PipeName, string LaunchFile, string LaunchArgs, int TimeoutMs)
{
    public static FileProxySettings FromAppSettings() => new(
        AppSettings.Default.FileProxyPipeName,
        AppSettings.Default.FileProxyLaunchFile,
        AppSettings.Default.FileProxyLaunchArgs,
        AppSettings.Default.FileProxyLaunchTimeoutMs);
};

/// <summary>一次 spool 的结果。</summary>
public sealed record SpoolResult(string SpoolFilePath, long BytesWritten);

/// <summary>一次 spool 的会话：连接 + open 完成、写盘未开始。超大文件确认框插在两者之间。</summary>
public sealed record SpoolSession(
    FileProxyClient Client,
    long Handle,
    long ExpectedSize,
    string OriginalPath,
    string SessionDir,
    string SpoolFile);

/// <summary>
/// 文件代理 spool 的落盘位置规则（spec「暂存位置与目录结构」，决策 D5）。
///
/// 根目录 = 应用所在目录下 <c>temp\logcat-proxy</c>（不提供配置项）：跟随应用所在盘，
/// 避开系统盘写满风险；exe 目录可写是既有前提（settings.json 同在此目录）。
/// 每次获取用一个独立 guid 子目录，创建即持有跨进程活性锁（决策 D1/D2）；
/// 崩溃残留由 <see cref="FileProxyService.CleanupStaleSpoolDirs"/> 按活性判定兜底。
/// </summary>
public static class SpoolPaths
{
    /// <summary>暂存根目录（应用目录\temp\logcat-proxy）。</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "temp", "logcat-proxy");

    /// <summary>活性锁文件名：session 目录内的固定名，随目录删除而消失，不留孤儿登记项（决策 D2）。</summary>
    internal const string LockFileName = ".alive.lock";

    /// <summary>新建一次获取的独立子目录并立即持有活性锁（句柄生命周期 = 目录生命周期）。</summary>
    public static SpoolDirLease NewSessionDir() => NewSessionDir(DefaultRoot);

    /// <summary>internal：指定根目录新建子目录（单测用临时根，不碰应用目录）。</summary>
    internal static SpoolDirLease NewSessionDir(string root) => NewSessionDir(root, null);

    /// <summary>
    /// internal：指定根目录新建 + 建锁前钩子（单测注入 D3 微秒窗口：建目录后、建锁前目录被并发清理删掉）。
    /// 建锁失败时重建目录重试一次（收窄窗口），二次仍失败才抛 I/O 错由打开路径报告。
    /// </summary>
    internal static SpoolDirLease NewSessionDir(string root, Action<string>? onBeforeLock)
    {
        Directory.CreateDirectory(root);
        string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        for (int attempt = 0; ; attempt++)
        {
            Directory.CreateDirectory(dir);
            onBeforeLock?.Invoke(dir);
            try
            {
                // FileShare.Delete：允许持有者自己删除所在目录（Close 先删目录、后释放锁）；
                // 同时仍拒绝任何其它句柄的读写访问 → 清理侧探测必然判「有主」
                var handle = new FileStream(Path.Combine(dir, LockFileName), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.Delete, bufferSize: 1);
                return new SpoolDirLease(dir, handle);
            }
            catch (IOException) when (attempt == 0)
            {
                // 目录在建锁前被并发清理删掉的微秒窗口：重建目录重试一次（决策 D3）
            }
        }
    }

    /// <summary>
    /// 跨进程活性探测（清理判据）：锁文件不存在 → 无主；存在且能被本进程排他打开 → 无主；
    /// 打不开 → 有主。Windows 文件共享校验按句柄、不按进程，故「本实例活跃目录」与
    /// 「他实例活跃目录」由同一判据覆盖（决策 D1）。三种输入均给确定结论，不抛未处理异常。
    /// </summary>
    internal static bool IsDirAlive(string dir)
    {
        try
        {
            var lockPath = Path.Combine(dir, LockFileName);
            if (!File.Exists(lockPath)) return false;
            try
            {
                using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;   // 打得开 = 无主（探测句柄随 using 立即关闭，不留下占用）
            }
            catch { return true; }   // 打不开 = 有主（被任何存活实例的排他句柄占着）
        }
        catch { return false; }   // 路径非法 / 目录并发消失：按无主（交删除路径报错留痕）
    }

    /// <summary>
    /// 暂存文件名：取原文件名；为空回退 <c>proxy.log</c>；超过 64 字符截断为
    /// 「头 48 + 尾 16」（尾段含扩展名），保证拼接后的暂存路径合法。
    /// </summary>
    public static string SpoolFileNameFor(string? originalPath)
    {
        string name;
        try { name = Path.GetFileName(originalPath ?? ""); }
        catch (ArgumentException) { name = ""; }   // 路径含非法字符时按空名处理
        if (name.Length == 0) return "proxy.log";
        if (name.Length <= 64) return name;
        return string.Concat(name.AsSpan(0, 48), name.AsSpan(name.Length - 16));
    }
}

/// <summary>
/// 一次暂存目录的租约：目录路径 + 目录内活性锁文件的排他句柄（决策 D1）。
/// 句柄生命周期 = 目录生命周期；不变式：<b>目录存在 ⇒ 锁仍持有</b>（决策 D4）——
/// 故调用方必须<b>先删除目录、后</b> <see cref="Release"/>。
/// </summary>
public sealed class SpoolDirLease : IDisposable
{
    readonly FileStream _handle;
    internal SpoolDirLease(string dir, FileStream handle) { Dir = dir; _handle = handle; }

    /// <summary>暂存目录路径。</summary>
    public string Dir { get; }

    /// <summary>释放活性锁（幂等）。删不掉目录时锁释放后残留目录无主，由下次启动清理收走。</summary>
    public void Release() => _handle.Dispose();

    public void Dispose() => Release();
}

/// <summary>
/// 文件代理使用判定（spec「代理使用判定」九格真值表）。
/// 纯函数：模式 × 直接打开结果 × 文本嗅探结果 → 是否走代理。
/// </summary>
public static class FileProxyDecision
{
    /// <summary>
    /// 九格真值表：开启恒走代理；自动在直接打开失败或嗅探判非文本时走代理；关闭恒不走。
    /// </summary>
    public static bool ShouldUseProxy(FileProxyMode mode, bool directOpenSucceeded, bool looksLikeText)
        => mode switch
        {
            FileProxyMode.On => true,
            FileProxyMode.Auto => !directOpenSucceeded || !looksLikeText,
            _ => false,   // Off：任何输入组合都不走代理（零行为变化的地基）
        };
}
