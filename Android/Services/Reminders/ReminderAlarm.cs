namespace FairyAI_Android.Services.Reminders;

/// <summary>
/// Platform-side alarm scheduling, so a reminder still fires when the app is not running.
///
/// Deliberately an interface with a null implementation: the in-process loop is the part that is
/// always true, and whether the platform can wake the app up is reported rather than assumed. A
/// build without an alarm scheduler says so in the UI instead of quietly missing reminders.
/// </summary>
public interface IReminderAlarm
{
    bool IsSupported { get; }

    string? LastError { get; }

    /// <summary>One line describing what the platform guarantees, for the reminders screen.</summary>
    string Describe { get; }

    void Schedule(Reminder reminder);

    void Cancel(string id);

    void CancelAll();
}

/// <summary>Used where the app cannot schedule anything: reminders only fire while it runs.</summary>
public sealed class NoReminderAlarm : IReminderAlarm
{
    public bool IsSupported => false;

    public string? LastError => "本平台没有接入系统闹钟";

    public string Describe => "只在应用运行时提醒";

    public void Schedule(Reminder reminder)
    {
    }

    public void Cancel(string id)
    {
    }

    public void CancelAll()
    {
    }
}
