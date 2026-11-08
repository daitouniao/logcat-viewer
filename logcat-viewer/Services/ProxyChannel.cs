namespace logcat.Services;

/// <summary>状态栏渠道状态区显示的装载渠道（三态互斥 + 空占位，spec「渠道状态区」）。</summary>
public enum ProxyChannel
{
    /// <summary>空：无文档、无采集、无 spool（空文本占位，不隐藏标签）。</summary>
    None,

    /// <summary>直接读：已打开非代理文档。</summary>
    LocalFile,

    /// <summary>文件代理：spool 在途，或当前文档经代理装载。</summary>
    FileProxy,

    /// <summary>ADB：采集在途（采集或增量索引入途）。</summary>
    Adb,
}

/// <summary>
/// 渠道状态三态互斥判定（spec「渠道状态区」）：ADB 在途优先 → spool 在途或代理文档 →
/// 直接读 → 空。纯函数，真值表 16 格全测（FileProxyChannelStateTests）。
/// </summary>
public static class ProxyChannelStates
{
    /// <summary>
    /// 三态互斥判定。优先级：ADB 在途 &gt; spool 在途或代理文档 &gt; 直接读 &gt; 空。
    /// spool 进行中即使文档尚未就位也判「文件代理读」（spec：spool 进行中即切代理读）。
    /// </summary>
    public static ProxyChannel Decide(bool adbActive, bool spoolActive, bool hasDoc, bool docProxied)
        => adbActive ? ProxyChannel.Adb
         : spoolActive || (hasDoc && docProxied) ? ProxyChannel.FileProxy
         : hasDoc ? ProxyChannel.LocalFile
         : ProxyChannel.None;

    /// <summary>渠道 → 状态栏文案。随语言切换由调用方按当前渠道重算（禁 static readonly 缓存文案）。</summary>
    public static string Text(ProxyChannel ch) => ch switch
    {
        ProxyChannel.Adb => Loc.T("ADB 采集"),
        ProxyChannel.FileProxy => Loc.T("文件代理读"),
        ProxyChannel.LocalFile => Loc.T("直接读文件"),
        _ => "",
    };
}
