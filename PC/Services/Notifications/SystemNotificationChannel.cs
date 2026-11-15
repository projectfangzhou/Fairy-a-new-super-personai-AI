using System.Diagnostics;

namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Delivers an ordinary notification to the operating system's own notification history.
///
/// This is deliberately a tiny seam rather than a platform SDK call sprinkled through the app: the
/// Windows side rides on the tray icon that already exists, the Linux side uses the standard
/// <c>notify-send</c>, and a platform that has neither reports itself unavailable instead of
/// pretending the message was shown.
/// </summary>
public interface ISystemNotificationChannel
{
    /// <summary>"windows", "linux", or "unavailable".</summary>
    string Platform { get; }

    bool IsAvailable { get; }

    /// <summary>Why the last delivery failed, when it did.</summary>
    string? LastError { get; }

    bool Show(string title, string body, NotificationSeverity severity);
}

/// <summary>
/// Wraps a host-provided delivery function. On Windows this is the tray icon's balloon, which the
/// shell turns into a normal Action Center notification; nothing here references WinForms, so the
/// notification logic stays testable and platform-free.
/// </summary>
public sealed class DelegateSystemNotificationChannel : ISystemNotificationChannel
{
    private readonly Func<string, string, NotificationSeverity, bool> _show;

    public DelegateSystemNotificationChannel(
        Func<string, string, NotificationSeverity, bool> show, string platform = "windows")
    {
        _show = show ?? throw new ArgumentNullException(nameof(show));
        Platform = platform;
    }

    public string Platform { get; }

    public bool IsAvailable { get; private set; } = true;

    public string? LastError { get; private set; }

    public bool Show(string title, string body, NotificationSeverity severity)
    {
        try
        {
            var shown = _show(title, body, severity);
            if (!shown) LastError = "宿主未能显示通知（托盘图标可能还没就绪）";
            return shown;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = $"显示通知时出错：{ex.GetType().Name}";
            return false;
        }
    }
}

/// <summary>
/// Linux delivery through <c>notify-send</c>. Arguments are passed as a list, never through a
/// shell, and a <c>--</c> separates them from the message so a title that starts with a dash cannot
/// turn into an option.
/// </summary>
public sealed class LibnotifySystemNotificationChannel : ISystemNotificationChannel
{
    private readonly Action<string, IReadOnlyList<string>> _run;

    public LibnotifySystemNotificationChannel(Action<string, IReadOnlyList<string>>? run = null,
        string executable = "notify-send")
    {
        Executable = executable;
        _run = run ?? RunProcess;
    }

    public string Executable { get; }

    public string Platform => "linux";

    public bool IsAvailable { get; private set; } = true;

    public string? LastError { get; private set; }

    public string[] BuildArguments(string title, string body, NotificationSeverity severity) =>
    [
        "--app-name=仙灵",
        "--urgency=" + Urgency(severity),
        "--",
        title,
        body,
    ];

    public bool Show(string title, string body, NotificationSeverity severity)
    {
        try
        {
            _run(Executable, BuildArguments(title, body, severity));
            return true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = $"调用 {Executable} 失败：{ex.GetType().Name}";
            return false;
        }
    }

    private static string Urgency(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => "critical",
        NotificationSeverity.Warning => "normal",
        _ => "low",
    };

    private static void RunProcess(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"{executable} 无法启动");
        process.WaitForExit(3000);
    }
}

/// <summary>A platform with no notification path. Says so; never silently swallows the attempt.</summary>
public sealed class UnavailableSystemNotificationChannel : ISystemNotificationChannel
{
    public UnavailableSystemNotificationChannel(string reason) => LastError = reason;

    public string Platform => "unavailable";

    public bool IsAvailable => false;

    public string? LastError { get; }

    public bool Show(string title, string body, NotificationSeverity severity) => false;
}
