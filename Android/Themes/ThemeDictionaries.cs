using Microsoft.Maui.Controls;

namespace FairyAI_Android.Themes;

/// <summary>
/// 四套主题字典的 C# 入口。
///
/// 为什么每个主题都要一个类：MAUI 的 <see cref="ResourceDictionary.Source"/> 只能在 XAML 里赋值，
/// 运行时写 <c>new ResourceDictionary { Source = ... }</c> 会抛
/// "Source can only be set from XAML"。所以要换主题，只能拿到<b>已经加载好的字典实例</b>——
/// 给每个主题文件加 x:Class，用构造函数实例化即可。ThemeManager 就是靠这几个类换字典的。
/// </summary>
public interface IThemeDictionary
{
    /// <summary>该字典对应的主题。</summary>
    FairyAI_Android.Services.FairyTheme Theme { get; }
}

public partial class ThemeMonoDark : ResourceDictionary, IThemeDictionary
{
    public ThemeMonoDark() => InitializeComponent();

    public FairyAI_Android.Services.FairyTheme Theme => FairyAI_Android.Services.FairyTheme.MonoDark;
}

public partial class ThemePureLight : ResourceDictionary, IThemeDictionary
{
    public ThemePureLight() => InitializeComponent();

    public FairyAI_Android.Services.FairyTheme Theme => FairyAI_Android.Services.FairyTheme.PureLight;
}

public partial class ThemeCyan : ResourceDictionary, IThemeDictionary
{
    public ThemeCyan() => InitializeComponent();

    public FairyAI_Android.Services.FairyTheme Theme => FairyAI_Android.Services.FairyTheme.Cyan;
}

public partial class ThemeEmerald : ResourceDictionary, IThemeDictionary
{
    public ThemeEmerald() => InitializeComponent();

    public FairyAI_Android.Services.FairyTheme Theme => FairyAI_Android.Services.FairyTheme.Emerald;
}
