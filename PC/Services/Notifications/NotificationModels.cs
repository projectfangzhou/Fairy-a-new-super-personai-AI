namespace MyAiAssistant.Services.Notifications;

/// <summary>What a notification is about. Anything not listed here does not belong in the HUD.</summary>
public enum NotificationKind
{
    TaskCompleted = 0,
    TaskFailed,
    ApprovalPending,
    QuestionPending,
    TransferIncoming,
    System
}

public enum NotificationSeverity
{
    Info = 0,
    Success,
    Warning,
    Error
}

/// <summary>A button the HUD may offer. The id is what the app knows how to do.</summary>
public sealed record NotificationAction(string Id, string Label);

/// <summary>
/// One notification.
///
/// This is the single contract the HUD, the control centre and the CLI read, the same way task
/// state has one shape: a second private event type per surface is exactly how two screens start
/// disagreeing about what happened.
///
/// <see cref="DedupeKey"/> is what keeps a task that reports the same thing twice from stacking up
/// duplicates, and <see cref="TargetId"/> is what a click should open (a task or an interaction).
/// </summary>
public sealed record NotificationEvent(
    string Id,
    NotificationKind Kind,
    NotificationSeverity Severity,
    string Title,
    string Body,
    string? TaskId,
    string? TargetId,
    IReadOnlyList<NotificationAction> Actions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    string DedupeKey)
{
    public bool IsUnread => ReadAt is null;

    public string Describe() => $"{Title}：{Body}";
}

/// <summary>Which notifications the user wants, and when they should stay quiet.</summary>
public sealed record NotificationPreferences(
    bool Enabled,
    bool TaskCompleted,
    bool TaskFailed,
    bool ApprovalPending,
    bool QuestionPending,
    bool TransferIncoming,
    int MaxEntries,
    bool QuietHoursEnabled,
    int QuietHoursStart,
    int QuietHoursEnd)
{
    public static NotificationPreferences Defaults { get; } = new(
        Enabled: true,
        TaskCompleted: true,
        TaskFailed: true,
        ApprovalPending: true,
        QuestionPending: true,
        TransferIncoming: true,
        MaxEntries: 200,
        QuietHoursEnabled: false,
        QuietHoursStart: 23,
        QuietHoursEnd: 7);

    /// <summary>Whether ordinary notifications may also go to the operating system's own history.</summary>
    public bool SystemEnabled { get; init; } = true;

    /// <summary>
    /// Mirror task events into the OS as well. Off by default: the checklist keeps key task
    /// notifications in the app's own layer, and the app's history is not the OS history.
    /// </summary>
    public bool MirrorKeyEvents { get; init; }

    /// <summary>
    /// Whether the OS notification may carry the actual text. Off by default, so a task title with a
    /// customer name or a file path does not end up in the Action Center.
    /// </summary>
    public bool SystemDetails { get; init; }

    public bool Allows(NotificationKind kind) => Enabled && kind switch
    {
        NotificationKind.TaskCompleted => TaskCompleted,
        NotificationKind.TaskFailed => TaskFailed,
        NotificationKind.ApprovalPending => ApprovalPending,
        NotificationKind.QuestionPending => QuestionPending,
        NotificationKind.TransferIncoming => TransferIncoming,
        _ => true
    };

    /// <summary>
    /// Quiet hours wrap past midnight, which is the case people actually configure (23:00 to 07:00).
    /// Approvals still get through: they are the one thing where staying silent blocks work.
    /// </summary>
    public bool IsQuiet(DateTimeOffset now)
    {
        if (!QuietHoursEnabled) return false;

        var hour = now.ToLocalTime().Hour;
        var inWindow = QuietHoursStart <= QuietHoursEnd
            ? hour >= QuietHoursStart && hour < QuietHoursEnd
            : hour >= QuietHoursStart || hour < QuietHoursEnd;

        return inWindow;
    }

    /// <summary>Whether this should be shown now. Recording and presenting are separate decisions.</summary>
    public bool ShouldPresent(NotificationKind kind, DateTimeOffset now)
    {
        if (!Allows(kind)) return false;
        if (!IsQuiet(now)) return true;

        return kind is NotificationKind.ApprovalPending or NotificationKind.QuestionPending;
    }
}

/// <summary>
/// The preferences store, as an interface so the notification centre can be tested without touching
/// the real configuration file.
/// </summary>
public interface INotificationSettings
{
    NotificationPreferences Read();

    (bool Ok, string Message) Apply(NotificationPreferences preferences);
}
