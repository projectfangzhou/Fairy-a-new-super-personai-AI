using System.Globalization;
using FairyAI_Android.Themes;

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

                // 所有 Theme.* 都写进同一个字典实例，切换主题是「改值」而不是「换字典」。
                //
                // 为什么不能换字典：实测在 MAUI 里对 MergedDictionaries 做 Remove + Add 之后，
                // 页面背景这类目标会刷新，而卡片的 BackgroundColor 不会——已存在的 DynamicResource
                // 目标收不到可靠通知。改成逐个键赋值，每改一个键都会触发 ValuesChanged，
                // 所有引用该键的目标都会重新取色。
                _live ??= new ResourceDictionary();

                if (!dictionaries.Contains(_live))
                {
                    // 第一次应用：把 App.xaml 里按路径合并的启动主题摘掉，换成这个稳定实例。
                    foreach (var stale in dictionaries.Where(IsBootstrapThemeDictionary).ToList())
                    {
                        dictionaries.Remove(stale);
                    }

                    dictionaries.Add(_live);
                }

                var source = Create(theme);
                foreach (var key in source.Keys)
                {
                    _live[key] = source[key];
                }

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

    /// <summary>稳定驻留在 MergedDictionaries 里的那一层主题字典，切主题只改它的值。</summary>
    private static ResourceDictionary? _live;

    /// <summary>
    /// App.xaml 里合并的那份启动主题：它是按路径合并的普通字典，不是 <see cref="IThemeDictionary"/>，
    /// 但第一次换主题时要一起摘掉，否则旧主题会一直留在合并列表里当垫底。
    /// </summary>
    private static bool IsBootstrapThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? string.Empty;
        return source.Contains("Styles/Themes/Theme", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按主题造一份新的字典实例。</summary>
    private static ResourceDictionary Create(FairyTheme theme) => theme switch
    {
        FairyTheme.PureLight => new ThemePureLight(),
        FairyTheme.Cyan => new ThemeCyan(),
        FairyTheme.Emerald => new ThemeEmerald(),
        _ => new ThemeMonoDark(),
    };

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

        // 改值只能保证之后新建的控件取到新颜色；已经在屏幕上的那些收不到可靠通知（实测：
        // 页面底色会变，卡片的 BackgroundColor 不会）。换主题这种低频操作，直接把 Shell 重建一次，
        // 让所有元素按新主题重新取值，比给每个页面写刷新逻辑可靠。
        RebuildShell(SettingsRoute);
    }

    private const string SettingsRoute = "settings";

    private static void RebuildShell(string route)
    {
        void Work()
        {
            try
            {
                if (Application.Current?.Windows.FirstOrDefault() is not { } window) return;

                var shell = new AppShell();
                window.Page = shell;
                shell.Dispatcher.Dispatch(async () =>
                {
                    try { await shell.GoToAsync("//" + route); }
                    catch (Exception ex) { Log($"重建后跳转失败：{ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                Log($"重建界面失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (MainThread.IsMainThread) Work();
        else MainThread.BeginInvokeOnMainThread(Work);
    }


    private static void Log(string message)    {
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
