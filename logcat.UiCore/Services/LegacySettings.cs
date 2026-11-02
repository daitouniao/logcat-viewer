using System;
using System.Collections.Generic;
using System.Drawing;
using logcat.Models;

namespace logcat.Services;

/// <summary>
/// 只读 legacy DTO：旧版（0.1.x）单文件 <c>settings.json</c> 的完整键面（无 SchemaVersion），
/// 仅供配置拆分迁移期读取，不提供任何写盘方法——迁移写盘走各落盘分支的新布局类。
/// 键面 = logcat 侧 10 键 + 安卓侧 11 键 + 语言 1 键（键名与旧版 AppSettings 完全一致）。
/// </summary>
public sealed class LegacySettings
{
    // ── logcat 侧（迁移后归 settings.json）──
    public bool AutoApply { get; set; } = true;
    public int FontPt { get; set; } = 10;
    public Point WindowLocation { get; set; }
    public Size WindowSize { get; set; }
    public int WindowState { get; set; }
    public string FileProxyMode { get; set; } = "";
    public string FileProxyPipeName { get; set; } = "logcat-fileproxy";
    public string FileProxyLaunchFile { get; set; } = "";
    public string FileProxyLaunchArgs { get; set; } = "--pipe {pipe}";
    public int FileProxyLaunchTimeoutMs { get; set; } = 5000;

    // ── 安卓侧（迁移后复制进 andtools.json）──
    public string LastLocalPath { get; set; } = "";
    public string LastRunAsRelayDir { get; set; } = "/sdcard/Download";
    List<DeviceLastDir> _deviceLastDirs = new();

    public List<DeviceLastDir> DeviceLastDirs
    {
        get => _deviceLastDirs;
        // 显式 null 也要归一为空集合：反序列化时 STJ 会用 JSON 的 null 覆盖字段初始值
        set => _deviceLastDirs = value ?? new List<DeviceLastDir>();
    }

    public List<int> DevicePaneColumnWidths { get; set; } = new();
    public List<int> LocalPaneColumnWidths { get; set; } = new();
    public bool ApkInstallViaPm { get; set; }
    public string ApkInstallFlags { get; set; } = "-r";
    public string ApkTmpDir { get; set; } = "/data/local/tmp";
    public bool ApkUninstallViaPm { get; set; }
    public bool ApkKeepData { get; set; }
    public bool ApkUseRoot { get; set; }

    // ── 共享键（迁移后抽出进 language.json）──
    public string Language { get; set; } = "";

    /// <summary>从旧布局文件读取；文件缺失或损坏返回 null（调用方按「全新安装不迁移」处理）。</summary>
    public static LegacySettings? Load(string path) => SettingsIO.TryLoad<LegacySettings>(path);
}
