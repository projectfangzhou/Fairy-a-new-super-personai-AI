using FairyAI_Android.Services.Storage;

namespace FairyAI_Android.Services.Reminders;

/// <summary>
/// The reminders file, and the only place reminder state changes.
///
/// A reminder time in the past is refused rather than stored: storing it would mean firing the moment
/// it is created, which reads as a bug to the user. Firing itself advances the record through
/// <see cref="ReminderSchedule.Advance"/>, so a repeating reminder moves forward by rule instead of
/// being recreated.
/// </summary>
public sealed class ReminderStore
{
    public const int MaxReminders = 200;
    public const int MaxTitleLength = 120;
    public const int MaxNoteLength = 2000;

    private readonly object _gate = new();
    private readonly List<Reminder> _reminders;
    private readonly Func<DateTime> _clock;

    public ReminderStore(string filePath, Func<DateTime>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("reminders need a file path", nameof(filePath));

        FilePath = filePath;
        _clock = clock ?? (() => DateTime.Now);
        _reminders = JsonListFile.Load<Reminder>(filePath, out var error);
        LoadError = error;

        // Records that cannot be a reminder (no id, no title, no time) are dropped rather than kept:
        // an entry with a default DueAt would otherwise read as "overdue since year 1" and fire.
        SkippedInvalid = _reminders.RemoveAll(item =>
            string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title) || item.DueAt == default);
    }

    public string FilePath { get; }

    public string? LoadError { get; }

    /// <summary>Records dropped on load because they were not a valid reminder.</summary>
    public int SkippedInvalid { get; }

    public string? LastError { get; private set; }

    public int Count
    {
        get { lock (_gate) return _reminders.Count; }
    }

    /// <summary>Still-enabled reminders, soonest first; finished ones after them, most recent first.</summary>
    public IReadOnlyList<Reminder> All()
    {
        lock (_gate)
        {
            var copy = _reminders.Select(Clone).ToList();
            copy.Sort((left, right) =>
            {
                if (left.Enabled != right.Enabled) return left.Enabled ? -1 : 1;
                return left.Enabled
                    ? left.DueAt.CompareTo(right.DueAt)
                    : right.DueAt.CompareTo(left.DueAt);
            });
            return copy;
        }
    }

    public Reminder? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_gate)
        {
            var reminder = _reminders.FirstOrDefault(item => item.Id == id);
            return reminder is null ? null : Clone(reminder);
        }
    }

    public (bool Ok, string Message, Reminder? Reminder) Add(ReminderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var title = (draft.Title ?? "").Trim();
        var note = (draft.Note ?? "").Trim();

        if (title.Length == 0 && note.Length == 0) return (false, "提醒没有内容", null);
        if (title.Length == 0) title = note.Split('\n')[0].Trim();
        if (title.Length > MaxTitleLength) return (false, $"提醒标题超过 {MaxTitleLength} 字", null);
        if (note.Length > MaxNoteLength) return (false, $"提醒备注超过 {MaxNoteLength} 字", null);

        var now = _clock();
        var due = draft.DueAt;

        if (due <= now)
            return (false, $"提醒时间已经过去了（现在是 {now:MM-dd HH:mm}）", null);

        if (due > now.AddYears(50))
            return (false, "提醒时间太远了", null);

        lock (_gate)
        {
            if (_reminders.Count(item => item.Enabled) >= MaxReminders)
                return (false, $"最多同时保留 {MaxReminders} 条提醒", null);

            var reminder = new Reminder
            {
                Id = "r" + Guid.NewGuid().ToString("n")[..12],
                Title = title,
                Note = note,
                DueAt = due,
                Repeat = draft.Repeat,
                Enabled = true,
            };

            _reminders.Add(reminder);

            if (Persist())
            {
                LastError = null;
                return (true, $"已设定提醒：{reminder.Title} · {ReminderSchedule.DescribeWhen(reminder.DueAt, now)}", Clone(reminder));
            }

            _reminders.Remove(reminder);
            return (false, LastError ?? "保存失败", null);
        }
    }

    public bool Cancel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_gate)
        {
            var reminder = _reminders.FirstOrDefault(item => item.Id == id);
            if (reminder is null) return false;

            _reminders.Remove(reminder);
            if (Persist())
            {
                LastError = null;
                return true;
            }

            _reminders.Add(reminder);
            return false;
        }
    }

    /// <summary>Dues that should fire now, soonest first, capped so a long absence cannot flood.</summary>
    public IReadOnlyList<Reminder> Due(DateTime now, int cap = 3)
    {
        lock (_gate)
        {
            return _reminders
                .Where(item => ReminderSchedule.IsDue(item, now))
                .OrderBy(item => item.DueAt)
                .Take(Math.Max(1, cap))
                .Select(Clone)
                .ToList();
        }
    }

    /// <summary>Records a delivery and moves the reminder forward. Null when it could not be persisted.</summary>
    public Reminder? MarkFired(string? id, DateTime firedAt)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_gate)
        {
            var reminder = _reminders.FirstOrDefault(item => item.Id == id);
            if (reminder is null) return null;

            var before = Clone(reminder);
            ReminderSchedule.Advance(reminder, firedAt);

            if (Persist())
            {
                LastError = null;
                return Clone(reminder);
            }

            CopyInto(reminder, before);
            return null;
        }
    }

    public int EnabledCount
    {
        get { lock (_gate) return _reminders.Count(item => item.Enabled); }
    }

    public string Describe(DateTime now)
    {
        lock (_gate)
        {
            var enabled = _reminders.Where(item => item.Enabled).ToList();
            var today = enabled.Count(item => item.DueAt.Date == now.Date);
            if (enabled.Count == 0) return "还没有提醒";

            return $"待提醒 {enabled.Count} 条 · 今天 {today} 条";
        }
    }

    private bool Persist()
    {
        if (JsonListFile.Save(FilePath, _reminders, out var error)) return true;

        LastError = error;
        return false;
    }

    private static void CopyInto(Reminder target, Reminder source)
    {
        target.Title = source.Title;
        target.Note = source.Note;
        target.DueAt = source.DueAt;
        target.Repeat = source.Repeat;
        target.Enabled = source.Enabled;
        target.LastFiredAt = source.LastFiredAt;
        target.FireCount = source.FireCount;
    }

    private static Reminder Clone(Reminder reminder) => new()
    {
        Id = reminder.Id,
        Title = reminder.Title,
        Note = reminder.Note,
        DueAt = reminder.DueAt,
        Repeat = reminder.Repeat,
        Enabled = reminder.Enabled,
        LastFiredAt = reminder.LastFiredAt,
        FireCount = reminder.FireCount,
    };
}
