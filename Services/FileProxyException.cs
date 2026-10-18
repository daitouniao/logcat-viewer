using System;

namespace logcat.Services;

/// <summary>
/// 文件代理失败的分类码（spec「失败分类与错误消息」）。
/// 每个码对应一条可执行的用户指引；<see cref="Io"/> 是兜底（I/O 原文透出，无专用错误码）。
/// </summary>
public enum FileProxyErrorCode
{
    /// <summary>未连接到文件代理：手动代理不在监听，且未配置启动命令。</summary>
    NoProxyNotRunning,

    /// <summary>已按配置启动代理进程，但等待超时管道始终未就绪。</summary>
    LaunchTimeout,

    /// <summary>实收字节与 open 响应的 size 不符（中途断线或文件变化）。</summary>
    ShortRead,

    /// <summary>hello 握手的协议版本缺失或不等于 1。</summary>
    ProtoMismatch,

    /// <summary>代理侧返回的错误（原文透出不翻译）。</summary>
    ProxyError,

    /// <summary>I/O 等通用错误（原文透出，无专用错误码——磁盘写满走这里）。</summary>
    Io,
}

/// <summary>
/// 文件代理链路的失败。消息在抛出点按当前语言生成（<see cref="Loc.F"/>），
/// 界面层另按 <see cref="Code"/> 给出分类指引（见 spec「失败分类与错误消息」）。
/// </summary>
public sealed class FileProxyException : Exception
{
    public FileProxyException(FileProxyErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>失败分类码。</summary>
    public FileProxyErrorCode Code { get; }

    /// <summary>代理侧原始错误描述（<see cref="FileProxyErrorCode.ProxyError"/> 时非空，原文透出不翻译）。</summary>
    public string? ProxyError { get; init; }

    /// <summary>完整命令行（<see cref="FileProxyErrorCode.LaunchTimeout"/> 时非空，便于排查）。</summary>
    public string? CommandLine { get; init; }

    /// <summary>短读实收字节数（<see cref="FileProxyErrorCode.ShortRead"/> 时非空）。</summary>
    public long? Received { get; init; }

    /// <summary>短读期望字节数（open 响应的 size，<see cref="FileProxyErrorCode.ShortRead"/> 时非空）。</summary>
    public long? Expected { get; init; }

    /// <summary>转代理前「直接打开」的原始异常（如有，附带供排查；分流段在代理路径失败后回填）。</summary>
    public Exception? DirectOpenError { get; set; }
}
