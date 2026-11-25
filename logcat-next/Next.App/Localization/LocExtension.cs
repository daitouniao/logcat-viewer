using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Next.App.Localization;

/// <summary>{loc Key=Menu.File}：返回指向 LocalizationService 索引器的单向绑定，
/// 语言切换随索引器通知（"Item"）自动刷新。</summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + Key + "]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
}
