using System.Xml.Linq;
using Xunit;

namespace Next.Tests;

/// <summary>
/// 语义 token 完整性（tasks 3.1）：明暗两套键集合一致、领域角色齐全、值都是颜色。
/// 直接解析源文件（仓库根注入），不依赖 Avalonia 运行时。
/// </summary>
public class ThemeTokenTests
{
    static string TokensPath()
    {
        var root = RepoRoot();
        return Path.Combine(root, "logcat-next", "Next.App", "Assets", "DesignTokens.axaml");
    }

    static string RepoRoot()
    {
        var attr = typeof(ThemeTokenTests).Assembly.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == "AssemblyMetadataAttribute"
                && (string)a.ConstructorArguments[0].Value! == "RepoRoot");
        return (string)attr!.ConstructorArguments[1].Value!;
    }

    static Dictionary<string, Dictionary<string, string>> ThemePalettes()
    {
        var doc = XDocument.Load(TokensPath());
        var xKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        var brush = XName.Get("SolidColorBrush", "https://github.com/avaloniaui");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var theme in doc.Descendants(XName.Get("ResourceDictionary", "https://github.com/avaloniaui"))
                     .Where(d => d.Attribute(xKey) is not null))
        {
            var name = theme.Attribute(xKey)!.Value;
            var palette = theme.Elements(brush)
                .ToDictionary(b => b.Attribute(xKey)!.Value, b => b.Attribute("Color")!.Value, StringComparer.Ordinal);
            result[name] = palette;
        }
        return result;
    }

    [Fact]
    public void LightAndDarkDefineSameKeys()
    {
        var palettes = ThemePalettes();
        Assert.True(palettes.ContainsKey("Light"), "缺少 Light 主题字典");
        Assert.True(palettes.ContainsKey("Dark"), "缺少 Dark 主题字典");
        Assert.Equal(
            palettes["Light"].Keys.OrderBy(k => k, StringComparer.Ordinal),
            palettes["Dark"].Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>领域角色必须两套都有：级别、标记、过滤提示、表面、文本（spec next/ui-theme）。</summary>
    [Fact]
    public void DomainRolesPresent()
    {
        var palettes = ThemePalettes();
        var required = new[]
        {
            "Surface.Canvas", "Surface.Panel", "Surface.HeaderRow", "Surface.Border", "Surface.Selection",
            "Text.Primary", "Text.Muted", "Accent",
            "Level.Verbose", "Level.Debug", "Level.Info", "Level.Warn",
            "Level.Error", "Level.Fatal", "Level.Assert", "Level.Unknown",
            "Mark.Background", "Filter.Or", "Filter.And", "Filter.CaseOn",
        };
        foreach (var (theme, palette) in palettes)
        {
            foreach (var key in required)
            {
                Assert.True(palette.ContainsKey(key), $"{theme} 缺少 token：{key}");
            }
        }
    }

    /// <summary>明暗两套的同一 token 颜色不同（若相同则该角色在另一主题下必然不可读）。</summary>
    [Fact]
    public void PalettesActuallyDiffer()
    {
        var palettes = ThemePalettes();
        var identical = palettes["Light"].Where(kv => palettes["Dark"][kv.Key] == kv.Value).ToList();
        Assert.Empty(identical);
    }
}
