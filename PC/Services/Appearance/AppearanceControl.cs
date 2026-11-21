using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.Theme;

namespace MyAiAssistant.Services.Appearance;

/// <summary>
/// Bridges the dashboard's appearance endpoint to the WPF theme manager.
///
/// It exists so the dashboard API stays free of any windowing dependency — it is compiled
/// into a plain test project that has no WPF stack at all.
/// </summary>
public sealed class AppearanceControl : IAppearanceControl
{
    public string CurrentTheme => ThemeManager.Current.ToString();

    public bool ApplyTheme(string themeName)
    {
        if (!Enum.TryParse<FairyTheme>(themeName, ignoreCase: true, out var theme)) return false;
        ThemeManager.SetTheme(theme);
        return true;
    }
}
