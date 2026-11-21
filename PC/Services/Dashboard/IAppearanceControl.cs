namespace MyAiAssistant.Services.Dashboard;

/// <summary>
/// The control centre's view of the app's appearance.
///
/// It exists so the dashboard API does not have to know about WPF: the API is compiled
/// into a plain test project with no windowing stack, and pulling in
/// <c>System.Windows.Application</c> would break that. The desktop app supplies the real
/// implementation; tests can leave it unset and the API reports that themes are unavailable.
/// </summary>
public interface IAppearanceControl
{
    /// <summary>The theme the app is currently on, as its enum name (for example "Cyan").</summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Applies a theme by name. Returns false when the name is not one of the four themes.
    /// </summary>
    bool ApplyTheme(string themeName);
}
