namespace MyAiAssistant.Services.Theme;

/// <summary>
/// 四套外观主题。与安卓端同一套命名与语义 —— 两端的颜色必须是一套，
/// 否则「手机上选翠、电脑上是墨黑」会显得是两套产品。
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

/// <summary>一个主题的全部颜色。玻璃层的参数也在这里，因为同一条 alpha 在浅底和深底上观感不同。</summary>
public sealed record ThemePalette
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsLight { get; init; }

    // 背景三层
    public required string Bg { get; init; }
    public required string BgMid { get; init; }
    public required string BgBottom { get; init; }

    // 面
    public required string SurfaceCard { get; init; }
    public required string SurfaceRaised { get; init; }
    public required string SurfaceSunken { get; init; }
    public required string SurfaceTile { get; init; }

    // 字
    public required string TextPrimary { get; init; }
    public required string TextSecondary { get; init; }
    public required string TextMuted { get; init; }
    public required string TextOnAccent { get; init; }

    // 强调与状态
    public required string Accent { get; init; }
    public required string AccentSoft { get; init; }
    public required string Success { get; init; }
    public required string Warning { get; init; }
    public required string Error { get; init; }
    public required string ErrorSurface { get; init; }

    // 线
    public required string Border { get; init; }
    public required string BorderStrong { get; init; }
    public required string Divider { get; init; }

    // 玻璃
    public required string GlassTint { get; init; }
    public required string GlassTintStrong { get; init; }
    public required string GlassHighlight { get; init; }
    public required string GlassBorder { get; init; }
    public required string GlassShadow { get; init; }
    public required string GlassSheen { get; init; }
}

public static class ThemeCatalog
{
    public static ThemePalette For(FairyTheme theme) => theme switch
    {
        FairyTheme.PureLight => new ThemePalette
        {
            Name = "素白",
            Description = "浅底 + 深青强调，白天更清楚",
            IsLight = true,
            Bg = "#F7F8FA", BgMid = "#FFFFFF", BgBottom = "#EEF1F5",
            SurfaceCard = "#FFFFFF", SurfaceRaised = "#FFFFFF", SurfaceSunken = "#F1F3F7", SurfaceTile = "#F4F6FA",
            TextPrimary = "#101828", TextSecondary = "#5A6478", TextMuted = "#8B94A6", TextOnAccent = "#FFFFFF",
            Accent = "#0E7490", AccentSoft = "#67C5DE", Success = "#0F9D63", Warning = "#B26A12",
            Error = "#C23B3B", ErrorSurface = "#FBEDED",
            Border = "#E3E7EF", BorderStrong = "#CBD2E0", Divider = "#EDF0F5",
            GlassTint = "#B3FFFFFF", GlassTintStrong = "#CCFFFFFF", GlassHighlight = "#FFFFFFFF",
            GlassBorder = "#1A101828", GlassShadow = "#14101828", GlassSheen = "#0AFFFFFF",
        },
        FairyTheme.Cyan => new ThemePalette
        {
            Name = "青",
            Description = "冷调深青，玻璃边缘与强调色同源",
            IsLight = false,
            Bg = "#04141A", BgMid = "#082028", BgBottom = "#0C2C36",
            SurfaceCard = "#0E2630", SurfaceRaised = "#143039", SurfaceSunken = "#0A1E26", SurfaceTile = "#0B222B",
            TextPrimary = "#E4F4F8", TextSecondary = "#93B7C4", TextMuted = "#5E818E", TextOnAccent = "#04141A",
            Accent = "#22D3EE", AccentSoft = "#0E7490", Success = "#5EEAD4", Warning = "#F0B872",
            Error = "#FCA5A5", ErrorSurface = "#2B1A22",
            Border = "#173C46", BorderStrong = "#1E5060", Divider = "#12313A",
            GlassTint = "#1422D3EE", GlassTintStrong = "#2422D3EE", GlassHighlight = "#4D67E8F5",
            GlassBorder = "#3322D3EE", GlassShadow = "#4D001418", GlassSheen = "#1F22D3EE",
        },
        FairyTheme.Emerald => new ThemePalette
        {
            Name = "翠",
            Description = "沉静的深绿，长时间阅读更舒适",
            IsLight = false,
            Bg = "#05140E", BgMid = "#0A1E16", BgBottom = "#0E2A1E",
            SurfaceCard = "#10231A", SurfaceRaised = "#163024", SurfaceSunken = "#0B1C14", SurfaceTile = "#0D2018",
            TextPrimary = "#E4F5EC", TextSecondary = "#96B9A6", TextMuted = "#628073", TextOnAccent = "#04140E",
            Accent = "#34D399", AccentSoft = "#0F7A52", Success = "#6EE7B7", Warning = "#E9B872",
            Error = "#F2A0A0", ErrorSurface = "#2A1A1E",
            Border = "#1B3B2C", BorderStrong = "#234B38", Divider = "#153226",
            GlassTint = "#1434D399", GlassTintStrong = "#2434D399", GlassHighlight = "#4D6EE7B7",
            GlassBorder = "#3334D399", GlassShadow = "#4D00140E", GlassSheen = "#1F34D399",
        },
        _ => new ThemePalette
        {
            Name = "墨黑",
            Description = "纯黑底 + 天蓝强调，夜间最省眼",
            IsLight = false,
            Bg = "#05070C", BgMid = "#0A0D12", BgBottom = "#0F131C",
            SurfaceCard = "#161D2B", SurfaceRaised = "#1E2636", SurfaceSunken = "#0F131C", SurfaceTile = "#111823",
            TextPrimary = "#E9E9E9", TextSecondary = "#9CA3AF", TextMuted = "#6B7280", TextOnAccent = "#0A0D12",
            Accent = "#38BDF8", AccentSoft = "#0E7490", Success = "#6EE7B7", Warning = "#E9A568",
            Error = "#FCA5A5", ErrorSurface = "#2A1F2F",
            Border = "#1F2937", BorderStrong = "#2A3547", Divider = "#1B2434",
            GlassTint = "#14FFFFFF", GlassTintStrong = "#22FFFFFF", GlassHighlight = "#3DFFFFFF",
            GlassBorder = "#26FFFFFF", GlassShadow = "#40000000", GlassSheen = "#1AFFFFFF",
        },
    };

    public static FairyTheme Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FairyTheme.MonoDark;
        return Enum.TryParse<FairyTheme>(raw, ignoreCase: true, out var parsed) ? parsed : FairyTheme.MonoDark;
    }
}
