using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 语言独立小文件 language.json 的存取（design D5，spec ui-language）：
/// <see cref="Loc.SetLang"/> 写、<see cref="Loc.DetectLang"/> 读都经 LanguageSettings。
/// 断言只涉及语言代码字符串与枚举（与 UI 输出无关），但按 CI 语言环境规则
/// 用 LangScope 双语各跑一遍「生成+断言」，保证两语言路径对称。
/// </summary>
public class LanguageStoreTests
{
    [Theory]
    [InlineData(UiLang.ZhCn)]
    [InlineData(UiLang.En)]
    public void 语言保存后读回_重启生效口径(UiLang lang)
    {
        using var f = new TempStoreFile(null);

        var s = LanguageSettings.LoadFrom(f.Path);
        Assert.Equal("", s.Language);              // 全新安装：无配置 → 系统区域兜底（这里锚「键面为空」）

        s.Language = Loc.LangCode(lang);
        s.Save();

        // 模拟另一进程（另一 exe）下次启动：重新从磁盘加载
        var again = LanguageSettings.LoadFrom(f.Path);
        Assert.Equal(Loc.LangCode(lang), again.Language);
        Assert.Equal(lang, Loc.ParseLang(again.Language));
    }

    [Fact]
    public void 落盘文件带SchemaVersion且不含其他键面()
    {
        using var f = new TempStoreFile(null);

        var s = LanguageSettings.LoadFrom(f.Path);
        s.Language = Loc.LangCode(UiLang.En);
        s.Save();

        var json = f.Read();
        Assert.Contains("\"SchemaVersion\": 2", json);
        Assert.DoesNotContain("FontPt", json);     // 语言文件只存语言，键面不回涨
    }

    [Fact]
    public void SetLang切换时语言代码与UI语言一致()
    {
        // SetLang(save:false) 不落盘（单元测试不写真实 language.json）；
        // 落盘路径已由上面用注入临时文件的用例覆盖。
        LangScope.InEn(() => Assert.Equal("en", Loc.LangCode(Loc.Current)));
        LangScope.InZh(() => Assert.Equal("zh-CN", Loc.LangCode(Loc.Current)));
    }
}
