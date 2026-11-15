using System.IO;
using System.Text.Json;

namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Keeps the notification history.
///
/// Two decisions worth stating. First, recording and presenting are separate: a notification the
/// user does not want to see right now (quiet hours) is still recorded, so the history is complete
/// and nothing silently disappears. Second, a repeat of the same event collapses into the existing
/// entry rather than adding another line — a task that reports "waiting for approval" three times is
/// one thing waiting, not three.
///
/// The history is the app's own, deliberately not mixed into the system notification history.
/// </summary>
public sealed class NotificationCenter
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly INotificationSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _path;
    private List<NotificationEvent> _entries = [];

    public NotificationCenter(INotificationSettings settings, string? directory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "notifications");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "history.json");

        _entries = Load();
    }

    /// <summary>Raised for every recorded notification, whether or not it should be shown now.</summary>
    public event Action<NotificationEvent>? Recorded;

    /// <summary>Raised only when the user should actually be interrupted.</summary>
    public event Action<NotificationEvent>? Presented;

    public string StorePath => _path;

    public int UnreadCount
    {
        get
        {
            lock (_gate) return _entries.Count(entry => entry.IsUnread);
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<NotificationEvent> Recent(int limit = 50)
    {
        lock (_gate)
        {
            return _entries
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(Math.Clamp(limit, 1, 500))
                .ToArray();
        }
    }

    /// <summary>
    /// Records a notification. Returns what is now stored, which may be an updated earlier entry
    /// when this is the same event again.
    /// </summary>
    public NotificationEvent Add(NotificationKind kind, string title, string body, string? taskId = null,
        string? targetId = null, IReadOnlyList<NotificationAction>? actions = null,
        NotificationSeverity severity = NotificationSeverity.Info, string? dedupeKey = null)
    {
        var preferences = _settings.Read();
        var now = _clock();

        var notification = new NotificationEvent(
            NewId(),
            kind,
            severity,
            Shorten(title, 80),
            Shorten(body, 300),
            taskId,
            targetId,
            actions ?? [],
            now,
            null,
            dedupeKey ?? $"{kind}:{taskId ?? targetId ?? title}");

        // A kind the user turned off is not recorded at all: keeping a history of something they
        // asked not to be told about is not respecting the setting.
        if (!preferences.Allows(kind)) return notification;

        NotificationEvent stored;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(entry =>
                entry.DedupeKey == notification.DedupeKey && now - entry.CreatedAt < DedupeWindow);

            if (existing is null)
            {
                _entries.Add(notification);
                stored = notification;
            }
            else
            {
                // Same event again: refresh the text and the time, keep it unread, keep one entry.
                stored = existing with
                {
                    Title = notification.Title,
                    Body = notification.Body,
                    Severity = notification.Severity,
                    Actions = notification.Actions,
                    CreatedAt = now,
                    ReadAt = null
                };
                _entries[_entries.IndexOf(existing)] = stored;
            }

            TrimTo(preferences.MaxEntries);
            SaveLocked();
        }

        Recorded?.Invoke(stored);
        if (preferences.ShouldPresent(kind, now)) Presented?.Invoke(stored);

        return stored;
    }

    /// <summary>Convenience for the bridge: same call, severity derived from the kind.</summary>
    public NotificationEvent Add(NotificationKind kind, string title, string body, string? taskId,
        string? targetId, NotificationSeverity severity) =>
        Add(kind, title, body, taskId, targetId, null, severity);

    public bool MarkRead(string id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return false;
            if (_entries[index].IsUnread) _entries[index] = _entries[index] with { ReadAt = _clock() };
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Marks the approval/question notifications that are no longer live. An entry whose interaction
    /// was answered elsewhere must stop looking like it still needs an answer.
    /// </summary>
    public int MarkResolvedInteractions(IReadOnlySet<string> liveTargetIds)
    {
        lock (_gate)
        {
            var now = _clock();
            var changed = 0;

            for (var index = 0; index < _entries.Count; index++)
            {
                var entry = _entries[index];
                if (!entry.IsUnread) continue;
                if (entry.Kind is not (NotificationKind.ApprovalPending or NotificationKind.QuestionPending)) continue;
                if (entry.TargetId is not null && liveTargetIds.Contains(entry.TargetId)) continue;

                _entries[index] = entry with { ReadAt = now };
                changed++;
            }

            if (changed > 0) SaveLocked();
            return changed;
        }
    }
    public int MarkAllRead()
    {
        lock (_gate)
        {
            var now = _clock();
            var changed = 0;
            for (var index = 0; index < _entries.Count; index++)
            {
                if (!_entries[index].IsUnread) continue;
                _entries[index] = _entries[index] with { ReadAt = now };
                changed++;
            }

            if (changed > 0) SaveLocked();
            return changed;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            var removed = _entries.Count;
            _entries.Clear();
            SaveLocked();
            return removed;
        }
    }

    private void TrimTo(int maxEntries)
    {
        var limit = Math.Clamp(maxEntries, 20, 2000);
        if (_entries.Count <= limit) return;
        _entries = _entries.OrderByDescending(entry => entry.CreatedAt).Take(limit).Reverse().ToList();
    }

    private List<NotificationEvent> Load()
    {
        if (!File.Exists(_path)) return [];

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<NotificationEvent>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable history is not worth failing startup over; keeping the file for
            // inspection is more useful than silently overwriting it.
            TryQuarantine();
            return [];
        }
    }

    private void TryQuarantine()
    {
        try
        {
            var target = _path + ".unreadable-" + _clock().ToUnixTimeSeconds();
            File.Copy(_path, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private void SaveLocked()
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing history is unpleasant, losing the running app is worse.
        }
    }

    private static string NewId() => "n" + Guid.NewGuid().ToString("N")[..12];

    private static string Shorten(string value, int max)
    {
        var single = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }
}
