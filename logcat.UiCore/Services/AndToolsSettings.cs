using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using logcat.Models;

namespace logcat.Services;

/// <summary>
/// 安卓工具箱侧设置（上次路径、文件浏览列宽、APK 安装/卸载选项），
/// 持久化到 exe 同目录 <c>andtools.json</c>（带 SchemaVersion，design D3/D4）。
/// 归属本文件的键与 logcat 侧、language.json 互不重叠：两应用并存保存互不覆盖（spec settings-storage）。
/// Default 单例不做迁移——落盘分支在各 exe（andtools 分支见 logcat.AndTools；logcat 分支会先写出本文件）。
/// </summary>
public sealed class AndToolsSettings
{
    static readonly string DefaultPath = Path.Combine(
        AppContext.BaseDirectory, "andtools.json");

    static AndToolsSettings? _default;

    public int SchemaVersion { get; set; } = SettingsIO.CurrentSchemaVersion;

    /// <summary>本实例的落盘路径。可注入，便于单元测试用临时文件（见 InternalsVisibleTo）。</summary>
    [JsonIgnore]
    public string StorePath { get; private set; } = DefaultPath;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static AndToolsSettings Default => _default ??= LoadFrom(DefaultPath);

    public AndToolsSettings() { }

    /// <summary>internal：指定落盘路径（单元测试与迁移原语传临时文件，见 InternalsVisibleTo）。</summary>
    internal AndToolsSettings(string path) => StorePath = path;

    internal static AndToolsSettings LoadFrom(string path)
    {
        var s = SettingsIO.TryLoad<AndToolsSettings>(path);
        if (s != null)
        {
            s.StorePath = path;    // 反序列化走无参构造，路径要重新绑到实际加载的文件
            return s;
        }
        return new AndToolsSettings(path);
    }

    // ── 上次路径 ──
    public string LastLocalPath { get; set; } = "";
    public string LastRunAsRelayDir { get; set; } = "/sdcard/Download";

    /// <summary>
    /// 按设备分别记忆的文件浏览目录，<b>最近在前</b>。
    ///
    /// <para>
    /// 用「列表 + 最近在前」而非「字典 + 键为序列号」：设备序列号并不稳定
    /// （模拟器端口号随启动顺序变化、网络设备地址随 IP 变化），记录会持续增殖，
    /// 字典没有天然的「最久未用」概念；列表按「最近使用置首、超限从尾部淘汰」维护，
    /// 与既有 <see cref="FavoritesStore"/> 的插前范式同构。
    /// </para>
    /// </summary>
    List<DeviceLastDir> _deviceLastDirs = new();

    public List<DeviceLastDir> DeviceLastDirs
    {
        get => _deviceLastDirs;
        // 显式 null 也要归一为空集合：反序列化时 STJ 会用 JSON 的 null 覆盖字段初始值
        set => _deviceLastDirs = value ?? new List<DeviceLastDir>();
    }

    /// <summary>按设备记忆目录的条数上限；超出后从尾部淘汰最久未用者。</summary>
    public const int MaxDeviceLastDirs = 16;

    /// <summary>取某设备记忆的目录；没有记录时返回空串。</summary>
    public string GetDeviceLastDir(string serial)
    {
        if (string.IsNullOrEmpty(serial)) return "";
        foreach (var e in DeviceLastDirs)
            if (string.Equals(e.Serial, serial, StringComparison.Ordinal))
                return e.Path;
        return "";
    }

    /// <summary>
    /// 记录某设备的目录：同设备的旧条目移除后置首（只留一条），
    /// 条数超上限时从尾部淘汰最久未用者。
    /// </summary>
    public void RecordDeviceLastDir(string serial, string path)
    {
        if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(path)) return;
        DeviceLastDirs.RemoveAll(e => string.Equals(e.Serial, serial, StringComparison.Ordinal));
        DeviceLastDirs.Insert(0, new DeviceLastDir { Serial = serial, Path = path });
        while (DeviceLastDirs.Count > MaxDeviceLastDirs)
            DeviceLastDirs.RemoveAt(DeviceLastDirs.Count - 1);
    }

    // ── 文件浏览列宽（设备端 / 本机面板各一组，元素为各列宽度，按列序对应）──
    public List<int> DevicePaneColumnWidths { get; set; } = new();
    public List<int> LocalPaneColumnWidths { get; set; } = new();

    // ── 安装/卸载窗口选项 ──
    public bool ApkInstallViaPm { get; set; }
    public string ApkInstallFlags { get; set; } = "-r";
    public string ApkTmpDir { get; set; } = "/data/local/tmp";
    public bool ApkUninstallViaPm { get; set; }
    public bool ApkKeepData { get; set; }
    public bool ApkUseRoot { get; set; }

    // ── 工具箱主窗体几何（N11：独立持久化，与日志查看器 settings.json 的窗口几何互不干扰）──
    public Point WindowLocation { get; set; }
    public Size WindowSize { get; set; }
    public int WindowState { get; set; }

    public void Save() => SaveTo(StorePath);

    /// <summary>internal：写回指定路径（单元测试与迁移原语传临时文件，见 InternalsVisibleTo）。
    /// 原子写（tmp+rename，见 <see cref="SettingsIO.AtomicWrite"/>）；磁盘不可写时吞异常仅留痕，
    /// 设置仅在当前会话生效——与既有存储类同一容错口径。</summary>
    internal void SaveTo(string path)
    {
        try
        {
            SettingsIO.AtomicWrite(path, JsonSerializer.Serialize(this, SettingsIO.JsonOpts));
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[AndToolsSettings] 保存失败 path={path} err={ex.Message}");
        }
    }
}
