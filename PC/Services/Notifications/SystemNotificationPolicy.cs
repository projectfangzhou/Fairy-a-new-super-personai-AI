using System.Text.RegularExpressions;

namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Decides which notifications may leave for the operating system, and what text they may carry.
///
/// The rule that matters: an approval or a question never goes to the OS. A toast cannot carry the
/// answer, and a stale "needs your approval" sitting in the Action Center long after it was answered
/// is worse than no notification at all. Those stay in the app's own layer, which is also the only
/// place that can act on them.
/// </summary>
public static class SystemNotificationPolicy
{
    /// <summary>Whether this kind may be delivered by the OS at all.</summary>
    public static bool IsSystemEligible(NotificationKind kind, NotificationPreferences preferences) => kind switch
    {
        NotificationKind.System => preferences.SystemEnabled,
        NotificationKind.TaskCompleted or NotificationKind.TaskFailed or NotificationKind.TransferIncoming =>
            preferences.SystemEnabled && preferences.MirrorKeyEvents,
        NotificationKind.ApprovalPending or NotificationKind.QuestionPending => false,
        _ => false
    };

    /// <summary>
    /// Builds the text the OS sees. With details off, task-derived text never leaves the app: the
    /// body becomes a fixed sentence, so a task title carrying a customer name or a file path cannot
    /// end up in the Action Center.
    /// </summary>
    public static (string Title, string Body) Compose(NotificationKind kind, string title, string body,
        bool includeDetails)
    {
        var safeTitle = Scrub(title, 60);

        // A System notification's text is written by us about our own state (an update is
        // available); it carries no task content, so it is worth showing even with details off.
        if (includeDetails || kind == NotificationKind.System)
            return (safeTitle, Scrub(body, 140));

        return (safeTitle, GenericBody(kind));
    }

    private static string GenericBody(NotificationKind kind) => kind switch
    {
        NotificationKind.TaskCompleted => "任务已完成，打开仙灵查看结果",
        NotificationKind.TaskFailed => "有任务失败，打开仙灵查看原因",
        NotificationKind.TransferIncoming => "有文件等你接收，打开仙灵处理",
        _ => "打开仙灵查看详情"
    };

    // Windows drive path, UNC path, and POSIX path. Kept narrow on purpose so ordinary prose is not
    // mangled; a false positive only hides a few words, a false negative leaks a path.
    private static readonly Regex WindowsPath = new(@"(?<![A-Za-z])[A-Za-z]:\\[^\s""'，。；、）)]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"\\\\[^\s""'，。；、）)]+", RegexOptions.Compiled);
    private static readonly Regex PosixPath = new(@"(?<=\s)/(?:[\w.\-]+/)+[\w.\-]*", RegexOptions.Compiled);

    // Anything that looks like a credential: long unbroken runs of token characters.
    private static readonly Regex LongToken = new(@"[A-Za-z0-9_\-]{32,}", RegexOptions.Compiled);

    public static string Scrub(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = WindowsPath.Replace(text, "（路径已省略）");
        text = UncPath.Replace(text, "（路径已省略）");
        text = PosixPath.Replace(text, "（路径已省略）");
        text = LongToken.Replace(text, "（已省略）");

        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
