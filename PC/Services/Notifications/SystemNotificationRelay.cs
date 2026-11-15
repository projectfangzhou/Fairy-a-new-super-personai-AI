namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Forwards the notifications that belong to the operating system, and nothing else.
///
/// It listens to <see cref="NotificationCenter.Recorded"/> rather than to the presenter, because a
/// notification the user silenced in the app is still a notification the user silenced: quiet hours
/// apply to the OS as well, not only to the HUD.
///
/// Every refusal is counted with a reason, so "I never saw a Windows notification" can be answered
/// instead of guessed at.
/// </summary>
public sealed class SystemNotificationRelay : IDisposable
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private readonly NotificationCenter _center;
    private readonly INotificationSettings _settings;
    private readonly ISystemNotificationChannel _channel;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastDelivered = new(StringComparer.Ordinal);
    private bool _disposed;

    public SystemNotificationRelay(NotificationCenter center, INotificationSettings settings,
        ISystemNotificationChannel channel, Func<DateTimeOffset>? clock = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _center.Recorded += OnRecorded;
    }

    public ISystemNotificationChannel Channel => _channel;

    /// <summary>How many notifications actually reached the operating system.</summary>
    public int Delivered { get; private set; }

    /// <summary>How many were considered and deliberately not delivered.</summary>
    public int Suppressed { get; private set; }

    /// <summary>Why the last attempt was suppressed. Read by the control centre.</summary>
    public string? LastSkipReason { get; private set; }

    private void OnRecorded(NotificationEvent notification)
    {
        if (_disposed) return;

        try
        {
            Deliver(notification);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // A notification must never take down the publisher.
            Note($"系统通知失败：{ex.GetType().Name}");
        }
    }

    private void Deliver(NotificationEvent notification)
    {
        if (!_channel.IsAvailable)
        {
            Note($"系统通知通道不可用：{_channel.LastError ?? "未知原因"}");
            return;
        }

        var preferences = _settings.Read();
        if (!preferences.SystemEnabled)
        {
            Note("系统通知已关闭");
            return;
        }

        if (!SystemNotificationPolicy.IsSystemEligible(notification.Kind, preferences))
        {
            Note("这类通知只在仙灵内提醒");
            return;
        }

        var now = _clock();
        if (preferences.IsQuiet(now))
        {
            Note("免打扰时段");
            return;
        }

        lock (_sync)
        {
            if (_lastDelivered.TryGetValue(notification.DedupeKey, out var last) && now - last < RepeatWindow)
            {
                Note("同一事项刚刚提醒过");
                return;
            }
        }

        var (title, body) = SystemNotificationPolicy.Compose(
            notification.Kind, notification.Title, notification.Body, preferences.SystemDetails);

        if (!_channel.Show(title, body, notification.Severity))
        {
            Note($"系统通知未能显示：{_channel.LastError ?? "通道未说明原因"}");
            return;
        }

        lock (_sync)
        {
            _lastDelivered[notification.DedupeKey] = now;
            Delivered++;
        }
    }

    private void Note(string reason)
    {
        lock (_sync)
        {
            Suppressed++;
            LastSkipReason = reason;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _center.Recorded -= OnRecorded;
    }
}
