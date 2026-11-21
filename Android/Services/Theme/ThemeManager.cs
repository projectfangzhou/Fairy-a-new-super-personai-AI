using System.Globalization;

namespace FairyAI_Android.Services.Theme;

/// <summary>
/// 运行时切换外观主题。
///
/// 实现要点：
///  · 主题字典只包含 <c>Theme.*</c> 键；语义别名（SurfaceCardBrush 等）在 Semantic.xaml 里
///    通过 DynamicResource 绑定到它们。切换时只需要换掉字典，语义层自动跟随。
///  · 切换必须在主线程做（ResourceDictionary 是 UI 对象）。
///  · 切换后不需要重建页面：所有页面用的是 DynamicResource，绑定会自己更新。
/// </summary>
public static class ThemeManager
{
    private static readonly object Gate = new();
    private static FairyTheme _current = FairyTheme.MonoDark;

    /// <summary>当前主题。</summary>
    public static FairyTheme Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>主题切换时触发，供原生层（状态栏配色）挂钩。</summary>
    public static event Action<FairyTheme>? ThemeChanged;

    /// <summary>
    /// 应用主题。重复设置同一主题会直接返回，不会重建资源字典。
    /// </summary>
    public static void Apply(FairyTheme theme)
    {
        lock (Gate)
        {
            if (_current == theme && _applied) return;
            _current = theme;
        }

        var app = Application.Current;
        if (app is null) return;

        void Work()
        {
            try
            {
                var resources = app.Resources;
                var dictionaries = resources.MergedDictionaries;

                // 先摘掉旧的主题字典。字典是按引用顺序合并的，后合并的覆盖先合并的，
                // 所以换主题只需要保证新的那一份排在后面。
                // MergedDictionaries 是 ICollection，没有索引器，所以先收集再逐个移除。
                var stale = dictionaries
                    .Where(d => IsThemeDictionary(d))
                    .ToList();

                foreach (var dictionary in stale)
                {
                    dictionaries.Remove(dictionary);
                }

                dictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(FairyThemeInfo.DictionaryPath(theme), UriKind.Relative),
                });

                // 应用级 UserAppTheme 决定系统控件（日期选择器、开关等）走哪套亮暗。
                // 四套主题里有两套是深底两套是浅底，所以要按主题本身给，而不是按 OS 主题给。
                app.UserAppTheme = FairyThemeInfo.IsLight(theme)
                    ? Microsoft.Maui.ApplicationModel.AppTheme.Light
                    : Microsoft.Maui.ApplicationModel.AppTheme.Dark;
            }
            catch (Exception ex)
            {
                Log($"主题切换失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (MainThread.IsMainThread) Work();
        else MainThread.BeginInvokeOnMainThread(Work);

        lock (Gate) _applied = true;
        ThemeChanged?.Invoke(theme);
    }

    private static bool _applied;

    /// <summary>判断一个合并进来的字典是不是主题字典。</summary>
    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? string.Empty;
        return source.Contains("Themes/Theme", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从配置读取并应用启动时的主题。</summary>
    public static void ApplyFromConfig()
    {
        try
        {
            Apply(FairyThemeInfo.Parse(ConfigManager.Load().Appearance.Theme));
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

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] [Theme] {message}\n");
        }
        catch (IOException)
        {
            // 日志写不进去不该影响界面。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
