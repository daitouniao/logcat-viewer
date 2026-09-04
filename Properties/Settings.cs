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
    }
}
