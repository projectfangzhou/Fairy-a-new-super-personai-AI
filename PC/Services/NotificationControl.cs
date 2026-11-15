using MyAiAssistant.Services.Notifications;

namespace MyAiAssistant.Services;

/// <summary>
/// The notification preferences as the app stores them, plus the history the control centre reads.
///
/// The centre itself takes an interface for settings, so it can be tested without the real
/// configuration file; this is the production side of that seam.
/// </summary>
public sealed class NotificationControl : INotificationSettings
{
    public NotificationPreferences Read()
    {
        var config = ConfigManager.Load().Notifications;

        return new NotificationPreferences(
            config.Enabled,
            config.TaskCompleted,
            config.TaskFailed,
            config.ApprovalPending,
            config.QuestionPending,
            config.TransferIncoming,
            config.MaxEntries,
            config.QuietHoursEnabled,
            Math.Clamp(config.QuietHoursStart, 0, 23),
            Math.Clamp(config.QuietHoursEnd, 0, 23))
        {
            SystemEnabled = config.SystemEnabled,
            MirrorKeyEvents = config.MirrorKeyEvents,
            SystemDetails = config.SystemDetails,
        };
    }

    public (bool Ok, string Message) Apply(NotificationPreferences preferences)
    {
        if (preferences.MaxEntries is < 20 or > 2000)
            return (false, "历史条数需在 20 到 2000 之间");

        if (preferences.QuietHoursStart is < 0 or > 23 || preferences.QuietHoursEnd is < 0 or > 23)
            return (false, "免打扰的起止时间需在 0 到 23 点之间");

        var config = ConfigManager.Load();
        config.Notifications.Enabled = preferences.Enabled;
        config.Notifications.TaskCompleted = preferences.TaskCompleted;
        config.Notifications.TaskFailed = preferences.TaskFailed;
        config.Notifications.ApprovalPending = preferences.ApprovalPending;
        config.Notifications.QuestionPending = preferences.QuestionPending;
        config.Notifications.TransferIncoming = preferences.TransferIncoming;
        config.Notifications.MaxEntries = preferences.MaxEntries;
        config.Notifications.QuietHoursEnabled = preferences.QuietHoursEnabled;
        config.Notifications.QuietHoursStart = preferences.QuietHoursStart;
        config.Notifications.QuietHoursEnd = preferences.QuietHoursEnd;
        config.Notifications.SystemEnabled = preferences.SystemEnabled;
        config.Notifications.MirrorKeyEvents = preferences.MirrorKeyEvents;
        config.Notifications.SystemDetails = preferences.SystemDetails;
        ConfigManager.Save(config);

        return (true, preferences.Enabled ? "通知设置已保存" : "通知已关闭，历史也不再记录");
    }
}
