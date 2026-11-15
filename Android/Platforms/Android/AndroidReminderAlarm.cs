#if ANDROID
using Android.App;
using Android.Content;
using FairyAI_Android.Services.Reminders;
using Application = Android.App.Application;

namespace FairyAI_Android.Services.Reminders;

/// <summary>
/// Schedules reminders through the system alarm so they fire with the app closed.
///
/// Inexact on purpose: <c>SetAndAllowWhileIdle</c> needs no SCHEDULE_EXACT_ALARM permission, and a
/// reminder arriving a minute late is a much smaller problem than an app that has to ask the user for
/// a special permission before it can remind anyone at all. The trade-off is stated in the reminders
/// screen rather than hidden here.
///
/// Alarms do not survive a reboot; <see cref="ReminderBootReceiver"/> puts them back.
/// </summary>
public sealed class AndroidReminderAlarm : IReminderAlarm
{
    internal const string ActionFire = "com.fairyai.assistant.action.REMINDER";
    internal const string ExtraId = "reminder_id";
    internal const string ExtraTitle = "reminder_title";
    internal const string ExtraBody = "reminder_body";

    private readonly Context _context;

    public AndroidReminderAlarm(Context? context = null) => _context = context ?? Application.Context;

    public bool IsSupported => true;

    public string? LastError { get; private set; }

    /// <summary>
    /// What the device will actually do, stated as it is. With the exact-alarm permission granted the
    /// alarm lands on time; without it Android batches inexact alarms and a reminder can be late by
    /// minutes or, for a time a day away, close to an hour.
    /// </summary>
    public string Describe => ExactAllowed
        ? "应用关闭也能提醒（准点）"
        : "应用关闭也能提醒，但系统可能推迟几分钟到一小时";

    private bool ExactAllowed
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return true; // exact alarms are free below 12

            try
            {
                var manager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
                return manager?.CanScheduleExactAlarms() ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Schedule(Reminder reminder)
    {
        if (reminder is null) return;

        try
        {
            var manager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
            if (manager is null)
            {
                LastError = "系统没有提供闹钟服务";
                return;
            }

            var pending = PendingIntentFor(reminder, PendingIntentFlags.UpdateCurrent);
            if (pending is null)
            {
                LastError = "无法创建闹钟的挂起意图";
                return;
            }

            var at = new DateTimeOffset(DateTime.SpecifyKind(reminder.DueAt, DateTimeKind.Local))
                .ToUnixTimeMilliseconds();

            if (ExactAllowed) manager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);
            else manager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);

            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"设定系统闹钟失败：{ex.GetType().Name}";
        }
    }

    public void Cancel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            var manager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
            var pending = PendingIntent.GetBroadcast(_context, RequestCode(id), IntentFor(id, null, null),
                PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);

            if (manager is null || pending is null) return;

            manager.Cancel(pending);
            pending.Cancel();
        }
        catch (Exception ex)
        {
            LastError = $"取消系统闹钟失败：{ex.GetType().Name}";
        }
    }

    public void CancelAll()
    {
        // Alarms are keyed by reminder id and the ids come from the file, so there is nothing to
        // enumerate here: every reminder that still exists gets rescheduled, and a stale alarm for a
        // deleted reminder fires once, finds no record and stays silent (see the receiver).
        LastError = null;
    }

    private PendingIntent? PendingIntentFor(Reminder reminder, PendingIntentFlags flags)
    {
        if (string.IsNullOrWhiteSpace(reminder.Id)) return null;

        var intent = IntentFor(reminder.Id, reminder.Title, reminder.Note);
        return PendingIntent.GetBroadcast(_context, RequestCode(reminder.Id), intent,
            flags | PendingIntentFlags.Immutable);
    }

    private Intent IntentFor(string id, string? title, string? body)
    {
        var intent = new Intent(_context, typeof(ReminderReceiver));
        intent.SetAction(ActionFire);
        intent.PutExtra(ExtraId, id);
        if (title is not null) intent.PutExtra(ExtraTitle, title);
        if (body is not null) intent.PutExtra(ExtraBody, body);
        return intent;
    }

    /// <summary>Stable per-reminder request code, so updating an alarm replaces it instead of adding one.</summary>
    internal static int RequestCode(string id)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in id) hash = hash * 31 + character;
            return hash & 0x7FFFFFFF;
        }
    }
}
#endif
