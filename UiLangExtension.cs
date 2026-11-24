using System;
using Avalonia.Markup.Xaml;

namespace StarryLauncher;

public class LangExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LangExtension() { }

    public LangExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider) => S.Get(Key);
}
