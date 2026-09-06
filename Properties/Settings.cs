namespace logcat.Properties
{
    [global::System.Configuration.SettingsProvider(typeof(global::System.Configuration.LocalFileSettingsProvider))]
    internal sealed class Settings : global::System.Configuration.ApplicationSettingsBase
    {
        public static Settings Default { get; } = new Settings();

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("True")]
        public bool Join { get => (bool)this["Join"]; set => this["Join"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("False")]
        public bool SingleLineExport { get => (bool)this["SingleLineExport"]; set => this["SingleLineExport"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("True")]
        public bool AutoApply { get => (bool)this["AutoApply"]; set => this["AutoApply"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("↵")]
        public string NewlineVis { get => (string)this["NewlineVis"]; set => this["NewlineVis"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("10")]
        public int FontPt { get => (int)this["FontPt"]; set => this["FontPt"] = value; }

        [global::System.Configuration.UserScopedSetting]
        public global::System.Drawing.Point WindowLocation
        {
            get => (global::System.Drawing.Point)this["WindowLocation"];
            set => this["WindowLocation"] = value;
        }

        [global::System.Configuration.UserScopedSetting]
        public global::System.Drawing.Size WindowSize
        {
            get => (global::System.Drawing.Size)this["WindowSize"];
            set => this["WindowSize"] = value;
        }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("0")]
        public int WindowState { get => (int)this["WindowState"]; set => this["WindowState"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("/sdcard")]
        public string LastRemotePath { get => (string)this["LastRemotePath"]; set => this["LastRemotePath"] = value; }

        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("")]
        public string LastLocalPath { get => (string)this["LastLocalPath"]; set => this["LastLocalPath"] = value; }

        /// <summary>run-as 模式上次使用的中转目录。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("/sdcard/Download")]
        public string LastRunAsRelayDir { get => (string)this["LastRunAsRelayDir"]; set => this["LastRunAsRelayDir"] = value; }

        /// <summary>安装/卸载窗口：是否走 pm 通道安装（adb install 被禁用时）。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("False")]
        public bool ApkInstallViaPm { get => (bool)this["ApkInstallViaPm"]; set => this["ApkInstallViaPm"] = value; }

        /// <summary>安装/卸载窗口：安装参数标记串，如 "-r -g"。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("-r")]
        public string ApkInstallFlags { get => (string)this["ApkInstallFlags"]; set => this["ApkInstallFlags"] = value; }

        /// <summary>安装/卸载窗口：pm install 推送 APK 的设备端临时目录。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("/data/local/tmp")]
        public string ApkTmpDir { get => (string)this["ApkTmpDir"]; set => this["ApkTmpDir"] = value; }

        /// <summary>安装/卸载窗口：是否走 pm 通道卸载。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("False")]
        public bool ApkUninstallViaPm { get => (bool)this["ApkUninstallViaPm"]; set => this["ApkUninstallViaPm"] = value; }

        /// <summary>安装/卸载窗口：卸载时是否保留数据和缓存（-k）。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("False")]
        public bool ApkKeepData { get => (bool)this["ApkKeepData"]; set => this["ApkKeepData"] = value; }

        /// <summary>安装/卸载窗口：pm 通道是否以 root（su -c）执行。</summary>
        [global::System.Configuration.UserScopedSetting]
        [global::System.Configuration.DefaultSettingValue("False")]
        public bool ApkUseRoot { get => (bool)this["ApkUseRoot"]; set => this["ApkUseRoot"] = value; }
    }
}
