namespace MyAiAssistant.Services.Screen;

/// <summary>
/// Whether this machine may be operated by the assistant at all.
///
/// The checklist allows Linux screen control only on sessions that have actually been verified
/// (Ubuntu 22.04/24.04, Debian 12, X11/Wayland with the right permissions). None of that has been
/// verified here, so Linux reports "not available" with the reason instead of failing one action at
/// a time — "disable and explain" rather than "pretend and hope".
/// </summary>
public sealed record ScreenControlAvailability(bool Supported, string Platform, string? Reason)
{
    public static ScreenControlAvailability Detect() => Detect(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    /// <summary>Overload with explicit facts, so the decision itself is testable on any machine.</summary>
    public static ScreenControlAvailability Detect(bool isWindows, bool isLinux, string? sessionType, string? desktop)
    {
        if (isWindows)
            return new ScreenControlAvailability(true, "windows", null);

        if (isLinux)
        {
            var session = string.IsNullOrWhiteSpace(sessionType) ? "未知会话" : sessionType!.Trim();
            var desktopName = string.IsNullOrWhiteSpace(desktop) ? "未知桌面" : desktop!.Trim();
            return new ScreenControlAvailability(false, $"linux({session}/{desktopName})",
                "Linux 屏幕操作尚未在 Ubuntu 22.04/24.04、Debian 12 的 X11/Wayland 会话上验证，按清单要求先禁用");
        }

        return new ScreenControlAvailability(false, OperatingSystem.IsMacOS() ? "macos" : "unknown",
            "当前平台没有可用的鼠标键盘注入实现");
    }
}
