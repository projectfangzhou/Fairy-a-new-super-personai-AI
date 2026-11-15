#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Application = Android.App.Application;

namespace FairyAI_Android.Services;

/// <summary>
/// Android's own notification history, through NotificationManagerCompat.
///
/// The permission check is not a formality: since Android 13 a notification posted without
/// POST_NOTIFICATIONS is dropped silently, and silently dropping is exactly what this project has
/// decided never to do. When it cannot post, <see cref="LastError"/> says why.
/// </summary>
public sealed class AndroidNotificationSink : INotificationSink
{
    public const string ChannelId = NotificationChannels.Summary;

    private readonly Context _context;
    private int _nextId;

    public AndroidNotificationSink(Context? context = null) =>
        _context = context ?? Application.Context;

    public bool IsSupported => true;

    public string? LastError { get; private set; }

    public bool Show(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            var channel = string.IsNullOrWhiteSpace(notification.Channel) ? NotificationChannels.Summary : notification.Channel;
            EnsureChannel(channel);

            var manager = NotificationManagerCompat.From(_context);
            if (manager is null || !manager.AreNotificationsEnabled())
            {
                LastError = "系统里的通知权限没有开启";
                return false;
            }

            using var intent = _context.PackageManager?.GetLaunchIntentForPackage(_context.PackageName ?? "");

            var builder = new NotificationCompat.Builder(_context, channel);
            builder?.SetContentTitle(notification.Title);
            builder?.SetContentText(notification.Body);
            builder?.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
            builder?.SetAutoCancel(true);
            builder?.SetPriority(notification.Important
                ? NotificationCompat.PriorityHigh
                : NotificationCompat.PriorityDefault);

            if (intent is not null)
            {
                var pending = PendingIntent.GetActivity(_context, 0, intent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                if (pending is not null) builder?.SetContentIntent(pending);
            }

            var built = builder?.Build();
            if (built is null)
            {
                LastError = "通知内容没能构造出来";
                return false;
            }

            manager.Notify(Interlocked.Increment(ref _nextId), built);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"发通知失败：{ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// One channel per purpose, so muting task summaries does not also mute the user's own reminders.
    /// </summary>
    private void EnsureChannel(string channelId)
    {
        // The analyzer only trusts this form; SdkInt comparisons are not recognised as guards.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
        if (manager is null || manager.GetNotificationChannel(channelId) is not null) return;

        var isReminder = channelId == NotificationChannels.Reminder;

        var channel = new NotificationChannel(
            channelId,
            isReminder ? "提醒" : "任务摘要",
            isReminder ? NotificationImportance.High : NotificationImportance.Default)
        {
            Description = isReminder
                ? "你自己设定的提醒时间到了"
                : "已配对电脑上任务状态的简短摘要",
        };

        manager.CreateNotificationChannel(channel);
    }
}
#endif
