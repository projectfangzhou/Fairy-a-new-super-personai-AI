namespace FairyAI_Android.Services;

/// <summary>Notification channels the app posts on. One channel per purpose so the user can mute one.</summary>
public static class NotificationChannels
{
    /// <summary>Task state changes synced from a paired PC.</summary>
    public const string Summary = "xianling_summary";

    /// <summary>The user's own reminders. Higher importance than a summary: it is time-critical.</summary>
    public const string Reminder = "xianling_reminder";
}

/// <summary>
/// One notification the phone should show. Deliberately tiny: no task output, no file path, no
/// prompt — the summary sync contract has nothing else to give, so there is nothing to leak here.
/// </summary>
public sealed record AppNotification(string Title, string Body, string Tag, bool Important)
{
    /// <summary>Which channel it goes to. Defaults to the summary channel the app had first.</summary>
    public string Channel { get; init; } = NotificationChannels.Summary;
}

/// <summary>
/// Posts a notification on the device. An interface so the sync logic stays testable and so the
/// platform that cannot show one reports it instead of pretending.
/// </summary>
public interface INotificationSink
{
    bool IsSupported { get; }

    string? LastError { get; }

    bool Show(AppNotification notification);
}

/// <summary>Used on platforms where this app has no notification path yet.</summary>
public sealed class NullNotificationSink : INotificationSink
{
    public bool IsSupported => false;

    public string? LastError => "本平台没有接入通知通道";

    public bool Show(AppNotification notification) => false;
}
