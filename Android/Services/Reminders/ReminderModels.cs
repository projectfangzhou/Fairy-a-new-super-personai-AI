namespace FairyAI_Android.Services.Reminders;

public enum ReminderRepeat
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,

    /// <summary>Every other week — the one extra rhythm that came up in real use; appended so the
    /// stored numbers of the existing rules never shift.</summary>
    EveryTwoWeeks = 4,
}

/// <summary>
/// One reminder. <see cref="DueAt"/> is the next local wall-clock time it should fire; for a
/// repeating reminder it moves forward each time it fires.
/// </summary>
public sealed class Reminder
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Note { get; set; } = "";

    public DateTime DueAt { get; set; }

    public ReminderRepeat Repeat { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastFiredAt { get; set; }

    public int FireCount { get; set; }
}

/// <summary>What a caller may set. Id null means "create"; a non-null id must exist.</summary>
public sealed record ReminderDraft(string Title, DateTime DueAt, ReminderRepeat Repeat = ReminderRepeat.None)
{
    public string Note { get; init; } = "";

    public string? Id { get; init; }
}

/// <summary>A reminder that fired, and how many of its occurrences had piled up unseen.</summary>
public sealed record ReminderFire(Reminder Reminder, int MissedOccurrences)
{
    public string Describe()
    {
        var when = ReminderSchedule.DescribeWhen(Reminder.DueAt);
        if (MissedOccurrences <= 1) return when;
        return $"{when}（错过 {MissedOccurrences - 1} 次，只补这一条）";
    }
}
