namespace logcat.Services;

/// <summary>文件代理模式（三态互斥）。</summary>
public enum FileProxyMode
{
    /// <summary>关闭：装载路径与未引入本能力时逐字节一致，零行为变化。</summary>
    Off = 0,

    /// <summary>开启：恒走代理（跳过直接读）。</summary>
    On = 1,

    /// <summary>自动：直接打开失败或嗅探判非文本时走代理，其余直接读。</summary>
    Auto = 2,
}

/// <summary>
/// 模式字符串解析（settings.json 的 <see cref="AppSettings.FileProxyMode"/> 原始值）。
///
/// **只认小写 "off" / "on" / "auto" 三个精确值**，其余一律 <see cref="FileProxyMode.Off"/>
/// ——含空串、前后空白（<c>"On "</c>）、大写（<c>"ON"</c>）与任意乱码。
/// 理由：解析失败不得导致代理被启用（spec「乱码配置回退关闭」场景逐字列出了
/// <c>"On "</c> 这类「看着像其实不是」的值），宁可漏放行，不误放行。
/// </summary>
public static class FileProxyModes
{
    public static FileProxyMode Parse(string? raw) => raw switch
    {
        "on" => FileProxyMode.On,
        "auto" => FileProxyMode.Auto,
        _ => FileProxyMode.Off,   // "off" / "" / 乱码
    };

    /// <summary>internal：模式 → 落盘原始串（设置对话框保存用，与 <see cref="Parse"/>  round-trip）。</summary>
    internal static string ToRaw(FileProxyMode mode) => mode switch
    {
        FileProxyMode.On => "on",
        FileProxyMode.Auto => "auto",
        _ => "off",
    };
}
