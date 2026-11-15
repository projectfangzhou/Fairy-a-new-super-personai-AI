#if ANDROID
using Android.App;
using Android.Content;
using FairyAI_Android.Services.Reminders;
using Microsoft.Extensions.DependencyInjection;

namespace FairyAI_Android.Services.Reminders;

/// <summary>
/// Receives the system alarm and turns it into a notification.
///
/// Two paths, and the choice between them is the whole point: when the app process is alive the
/// receiver asks the running <see cref="ReminderService"/> to do its usual check, so there is one
/// owner of reminder state and no second copy of the file in memory. Only when the process was
/// started just for this broadcast (app was closed) does the receiver do the work itself, reading
/// and writing the same JSON file the app would.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;

        try
        {
            var id = intent.GetStringExtra(AndroidReminderAlarm.ExtraId);
            var title = intent.GetStringExtra(AndroidReminderAlarm.ExtraTitle);
            var body = intent.GetStringExtra(AndroidReminderAlarm.ExtraBody);

            if (string.IsNullOrWhiteSpace(id)) return;

            if (RunningReminders() is { } running)
            {
                _ = running.CheckAsync();
                return;
            }

            var store = new ReminderStore(ReminderFile(context));
            var reminder = store.Get(id);

            // A record removed after the alarm was set: nothing to remind about.
            if (reminder is null || !reminder.Enabled) return;

            var sink = new AndroidNotificationSink(context);
            var notification = new AppNotification(
                Title: string.IsNullOrWhiteSpace(title) ? reminder.Title : title!,
                Body: string.IsNullOrWhiteSpace(body) ? ReminderSchedule.DescribeWhen(reminder.DueAt) : body!,
                Tag: id!,
                Important: true)
            {
                Channel = NotificationChannels.Reminder,
            };

            // Post first, record second: a delivered notification that fails to persist shows up once
            // more later, while the reverse order loses it entirely.
            sink.Show(notification);

            var updated = store.MarkFired(id, DateTime.Now);
            if (updated is { Enabled: true }) new AndroidReminderAlarm(context).Schedule(updated);
        }
        catch (Exception)
        {
            // A broadcast receiver that throws is killed by the system; there is nowhere to report to
            // but the notification history the user already has.
        }
    }

    internal static string ReminderFile(Context context) =>
        Path.Combine(context.FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory, "reminders.json");

    private static ReminderService? RunningReminders()
    {
        try
        {
            return IPlatformApplication.Current?.Services?.GetService<ReminderService>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Puts the alarms back after a reboot or an app update — the system drops them, the reminders file
/// does not.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public sealed class ReminderBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;

        try
        {
            var running = IPlatformApplication.Current?.Services?.GetService<ReminderService>();
            if (running is not null)
            {
                running.RescheduleAll();
                return;
            }

            var store = new ReminderStore(ReminderReceiver.ReminderFile(context));
            var alarm = new AndroidReminderAlarm(context);

            foreach (var reminder in store.All().Where(item => item.Enabled))
                alarm.Schedule(reminder);
        }
        catch (Exception)
        {
            // Same reasoning as the receiver above: nothing useful to do inside a broadcast.
        }
    }
}
#endif
