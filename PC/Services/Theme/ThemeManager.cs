using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MyAiAssistant.Services.Theme;

/// <summary>
/// 运行时切换外观主题。
///
/// WPF 这边不用字典文件，而是直接建 ResourceDictionary 往 Application.Resources 里换。
/// 原因：字典文件要重新加载，还要处理 XAML 里 StaticResource 无法更新的问题；
/// 直接换字典对象，所有 DynamicResource 绑定会自动收到通知并刷新，
/// 页面不需要重建。
///
/// 所有窗口的配色都必须走 DynamicResource 而不是硬编码 hex，否则主题切换只改得动一半。
/// </summary>
public static class ThemeManager
{
    private static readonly object Gate = new();
    private static FairyTheme _current = FairyTheme.MonoDark;
    private static bool _applied;

    /// <summary>当前主题。</summary>
    public static FairyTheme Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>当前主题的配色。</summary>
    public static ThemePalette Palette => ThemeCatalog.For(Current);

    /// <summary>主题切换时触发，供那些自己画的控件（波形、辉光）跟着换色。</summary>
    public static event Action<FairyTheme>? ThemeChanged;

    /// <summary>应用主题。重复设置同一主题会直接返回。</summary>
    public static void Apply(FairyTheme theme)
    {
        lock (Gate)
        {
            if (_current == theme && _applied) return;
            _current = theme;
        }

        var app = Application.Current;
        if (app is null) return;

        var palette = ThemeCatalog.For(theme);
        var resources = app.Resources;

        // 先清掉上一套，避免旧键残留。
        var previous = resources.MergedDictionaries
            .OfType<ResourceDictionary>()
            .FirstOrDefault(d => d.Contains("SurfaceCardBrush"));

        if (previous is not null)
        {
            resources.MergedDictionaries.Remove(previous);
        }

        var dictionary = Build(palette);
        resources.MergedDictionaries.Add(dictionary);

        lock (Gate) _applied = true;
        ThemeChanged?.Invoke(theme);

        // 自绘的控件自己拿不到 DynamicResource，得显式通知它们重画。
        try
        {
            Controls.WaveformVisualizer.RefreshTheme();
        }
        catch (Exception ex)
        {
            Log($"波形换色失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>从配置读取并应用启动时的主题。</summary>
    public static void ApplyFromConfig()
    {
        try
        {
            Apply(ThemeCatalog.Parse(ConfigManager.Load().Appearance.Theme));
        }
        catch (Exception ex)
        {
            Log($"启动主题应用失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>切换主题并写回配置。</summary>
    public static void SetTheme(FairyTheme theme)
    {
        Apply(theme);
        try
        {
            var config = ConfigManager.Load();
            config.Appearance.Theme = theme.ToString();
            ConfigManager.Save(config);
        }
        catch (Exception ex)
        {
            Log($"主题保存失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把一套配色变成 ResourceDictionary。</summary>
    private static ResourceDictionary Build(ThemePalette p)
    {
        var dictionary = new ResourceDictionary();

        void Brush(string key, string hex) => dictionary[key] = new SolidColorBrush(ToColor(hex));
        void ColorKey(string key, string hex) => dictionary[key] = ToColor(hex);

        // 背景
        Brush("BgBrush", p.Bg);
        Brush("BgMidBrush", p.BgMid);
        Brush("BgBottomBrush", p.BgBottom);

        // 面
        Brush("SurfaceCardBrush", p.SurfaceCard);
        Brush("SurfaceRaisedBrush", p.SurfaceRaised);
        Brush("SurfaceSunkenBrush", p.SurfaceSunken);
        Brush("SurfaceTileBrush", p.SurfaceTile);
        Brush("ErrorSurfaceBrush", p.ErrorSurface);

        // 字
        Brush("TextPrimaryBrush", p.TextPrimary);
        Brush("TextSecondaryBrush", p.TextSecondary);
        Brush("TextMutedBrush", p.TextMuted);
        Brush("TextOnAccentBrush", p.TextOnAccent);

        // 强调与状态
        Brush("AccentBrush", p.Accent);
        Brush("AccentSoftBrush", p.AccentSoft);
        Brush("SuccessBrush", p.Success);
        Brush("WarningBrush", p.Warning);
        Brush("ErrorBrush", p.Error);

        // 线
        Brush("BorderBrush", p.Border);
        Brush("BorderStrongBrush", p.BorderStrong);
        Brush("DividerBrush", p.Divider);

        // 玻璃
        Brush("GlassTintBrush", p.GlassTint);
        Brush("GlassTintStrongBrush", p.GlassTintStrong);
        Brush("GlassHighlightBrush", p.GlassHighlight);
        Brush("GlassBorderBrush", p.GlassBorder);
        Brush("GlassShadowBrush", p.GlassShadow);
        Brush("GlassSheenBrush", p.GlassSheen);

        // 颜色本身也暴露出去：代码里画东西（波形、辉光）时用得到。
        ColorKey("Theme.Bg", p.Bg);
        ColorKey("Theme.SurfaceCard", p.SurfaceCard);
        ColorKey("Theme.SurfaceRaised", p.SurfaceRaised);
        ColorKey("Theme.SurfaceSunken", p.SurfaceSunken);
        ColorKey("Theme.TextPrimary", p.TextPrimary);
        ColorKey("Theme.TextSecondary", p.TextSecondary);
        ColorKey("Theme.TextMuted", p.TextMuted);
        ColorKey("Theme.TextOnAccent", p.TextOnAccent);
        ColorKey("Theme.Accent", p.Accent);
        ColorKey("Theme.AccentSoft", p.AccentSoft);
        ColorKey("Theme.Success", p.Success);
        ColorKey("Theme.Warning", p.Warning);
        ColorKey("Theme.Error", p.Error);
        ColorKey("Theme.Border", p.Border);
        ColorKey("Theme.BorderStrong", p.BorderStrong);
        ColorKey("Theme.Divider", p.Divider);
        ColorKey("Theme.GlassTint", p.GlassTint);
        ColorKey("Theme.GlassTintStrong", p.GlassTintStrong);
        ColorKey("Theme.GlassHighlight", p.GlassHighlight);
        ColorKey("Theme.GlassBorder", p.GlassBorder);
        ColorKey("Theme.GlassShadow", p.GlassShadow);
        ColorKey("Theme.GlassSheen", p.GlassSheen);

        return dictionary;
    }

    /// <summary>取当前主题里的某个颜色（代码里画东西时用）。</summary>
    public static Color GetColor(string key, Color fallback)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return fallback;
        if (resources.Contains(key) && resources[key] is Color color) return color;
        return fallback;
    }

    /// <summary>取当前主题里的某个画刷。</summary>
    public static Brush GetBrush(string key, Brush fallback)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return fallback;
        if (resources.Contains(key) && resources[key] is Brush brush) return brush;
        return fallback;
    }

    private static Color ToColor(string hex)
    {
        try
        {
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Colors.Transparent;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FairyAI", "fairy.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] [Theme] {message}\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
