using FairyAI_Android.Services.Reminders;

namespace FairyAI_Android.Services;

/// <summary>
/// Fires reminders and posts them as notifications.
///
/// The single rule that shapes this class: a reminder is only marked as delivered when the
/// notification actually reached the user. If the platform refuses it (permission off, channel
/// muted, no sink), the record stays due and the reason is reported — a reminder that silently
/// burns itself looks exactly like one that never existed.
///
/// The in-process loop is the baseline that always works while the app is alive. When the platform
/// provides <see cref="IReminderAlarm"/>, each reminder is also handed to the system alarm so it
/// fires with the app closed; the loop then stays as the catch-up path.
/// </summary>
public sealed class ReminderService : IDisposable
{
    /// <summary>How many notifications one check may post, so a long absence cannot flood the shade.</summary>
    public const int MaxNotificationsPerCheck = 3;

    private readonly INotificationSink _sink;
    private readonly ReminderStore _store;
    private readonly IReminderAlarm? _alarm;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private CancellationTokenSource? _cts;

    public ReminderService(INotificationSink sink, ReminderStore store, IReminderAlarm? alarm = null,
        Func<DateTime>? clock = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _alarm = alarm;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>How often the in-process loop looks for due reminders.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    public ReminderStore Store => _store;

    public bool IsRunning => _cts is not null;

    /// <summary>Notifications actually posted.</summary>
    public int Delivered { get; private set; }

    /// <summary>Checks run, including the ones that found nothing.</summary>
    public int Checks { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised after a reminder fired or the list changed, so a visible page can refresh.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        RescheduleAll();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        // Alarms are deliberately left in place: closing the app is exactly when they matter.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not an error.
        }
    }

    /// <summary>Fires whatever is due. Returns how many notifications were delivered.</summary>
    public async Task<int> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;

        try
        {
            Checks++;

            if (!_sink.IsSupported)
            {
                LastError = _sink.LastError ?? "本机无法显示通知";
                return 0;
            }

            var now = _clock();
            var due = _store.Due(now, MaxNotificationsPerCheck);
            var delivered = 0;

            foreach (var reminder in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var missed = ReminderSchedule.MissedOccurrences(reminder, now);
                var notification = Build(reminder, missed);

                if (!_sink.Show(notification))
                {
                    LastError = _sink.LastError ?? "通知没有发出去";
                    continue;
                }

                delivered++;
                Delivered++;

                var updated = _store.MarkFired(reminder.Id, now);
                if (updated is null)
                {
                    LastError = _store.LastError ?? "提醒状态没能保存";
                    continue;
                }

                if (_alarm is not null)
                {
                    if (updated.Enabled) _alarm.Schedule(updated);
                    else _alarm.Cancel(updated.Id);
                }
            }

            if (delivered > 0)
            {
                LastError = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return delivered;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public (bool Ok, string Message, Reminder? Reminder) Add(ReminderDraft draft)
    {
        var result = _store.Add(draft);
        if (!result.Ok || result.Reminder is null) return result;

        if (_alarm is not null)
        {
            var alarm = _alarm;
            if (alarm.IsSupported) alarm.Schedule(result.Reminder);
            else LastError = alarm.LastError;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool Cancel(string? id)
    {
        var removed = _store.Cancel(id);
        if (!removed) return false;

        _alarm?.Cancel(id ?? "");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<Reminder> All() => _store.All();

    public string Describe() => _store.Describe(_clock()) + " · " + DescribeScheduling();

    public string DescribeScheduling()
    {
        if (_alarm is null) return "只在应用运行时提醒";

        return _alarm.IsSupported ? _alarm.Describe : _alarm.LastError ?? "只在应用运行时提醒";
    }

    /// <summary>Hands every enabled reminder to the platform again — after a restart, alarms are gone.</summary>
    public void RescheduleAll()
    {
        if (_alarm is null) return;

        _alarm.CancelAll();
        if (!_alarm.IsSupported) return;

        foreach (var reminder in _store.All().Where(item => item.Enabled))
            _alarm.Schedule(reminder);
    }

    private AppNotification Build(Reminder reminder, int missed)
    {
        var when = ReminderSchedule.DescribeWhen(reminder.DueAt, _clock());
        var repeat = reminder.Repeat == ReminderRepeat.None ? "" : " · " + ReminderSchedule.DescribeRepeat(reminder.Repeat);
        var body = string.IsNullOrWhiteSpace(reminder.Note) ? when + repeat : reminder.Note + " · " + when + repeat;

        if (missed > 1) body += $"（错过 {missed - 1} 次，只补这一条）";

        return new AppNotification(reminder.Title, body, reminder.Id, Important: true)
        {
            Channel = NotificationChannels.Reminder,
        };
    }

    public void Dispose()
    {
        Stop();
        _checkGate.Dispose();
    }
}
