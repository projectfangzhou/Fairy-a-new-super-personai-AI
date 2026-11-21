namespace FairyAI_Android.Services;

/// <summary>
/// 四套外观主题。名字沿用界面上的说法，不要在代码里用「dark/light」这种二元的叫法——
/// 那会暗示只有明暗两极，而实际上「墨黑/素白」和「青/翠」是两条不同的轴。
/// </summary>
public enum FairyTheme
{
    /// <summary>墨黑：纯黑底 + 天蓝强调。</summary>
    MonoDark = 0,

    /// <summary>素白：浅底 + 深青强调。</summary>
    PureLight = 1,

    /// <summary>青：冷调深青底 + 亮青强调。</summary>
    Cyan = 2,

    /// <summary>翠：深绿底 + 薄荷绿强调。</summary>
    Emerald = 3,
}

public static class FairyThemeInfo
{
    /// <summary>设置页里显示的名字。</summary>
    public static string DisplayName(FairyTheme theme) => theme switch
    {
        FairyTheme.MonoDark => "墨黑",
        FairyTheme.PureLight => "素白",
        FairyTheme.Cyan => "青",
        FairyTheme.Emerald => "翠",
        _ => "墨黑",
    };

    /// <summary>设置页里显示的一句说明。</summary>
    public static string Description(FairyTheme theme) => theme switch
    {
        FairyTheme.MonoDark => "纯黑底 + 天蓝强调，夜间最省眼",
        FairyTheme.PureLight => "浅底 + 深青强调，白天更清楚",
        FairyTheme.Cyan => "冷调深青，玻璃边缘与强调色同源",
        FairyTheme.Emerald => "沉静的深绿，长时间阅读更舒适",
        _ => "纯黑底 + 天蓝强调，夜间最省眼",
    };

    /// <summary>该主题的浅色背景（用于状态栏图标等系统级反色判断）。</summary>
    public static bool IsLight(FairyTheme theme) => theme == FairyTheme.PureLight;

    /// <summary>该主题的强调色 hex，用于原生层（状态栏、导航栏）取色。</summary>
    public static string AccentHex(FairyTheme theme) => theme switch
    {
        FairyTheme.PureLight => "#0E7490",
        FairyTheme.Cyan => "#22D3EE",
        FairyTheme.Emerald => "#34D399",
        _ => "#38BDF8",
    };

    public static FairyTheme Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FairyTheme.MonoDark;
        return Enum.TryParse<FairyTheme>(raw, ignoreCase: true, out var parsed) ? parsed : FairyTheme.MonoDark;
    }
}
