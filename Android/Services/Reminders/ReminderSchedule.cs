using System.Globalization;

namespace FairyAI_Android.Services.Reminders;

/// <summary>
/// When a reminder is next due, and whether it is due now. Pure date arithmetic on the user's local
/// wall clock — no time zones, no DST handling, because a reminder set for 09:00 means 09:00 on the
/// wall clock the user is looking at.
///
/// Monthly repeat clamps to the end of short months (31st → 28/29/30), and always counts from the
/// original due date rather than from the clamped one, so a reminder on the 31st does not drift to
/// the 28th forever after one February.
/// </summary>
public static class ReminderSchedule
{
    /// <summary>Guard so a corrupted record cannot spin: no repeat needs more than this.</summary>
    private const int MaxAdvanceSteps = 5000;

    private static readonly string[] FullFormats =
    {
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyyMMddHHmm",
    };

    private static readonly string[] ShortDateFormats = { "MM-dd HH:mm", "MM/dd HH:mm" };

    private static readonly string[] TimeOnlyFormats = { "HH:mm", "HH:mm:ss" };

    /// <summary>Strictly after <paramref name="after"/>; null when a one-shot time is already past.</summary>
    public static DateTime? NextOccurrence(ReminderRepeat repeat, DateTime from, DateTime after)
    {
        if (repeat == ReminderRepeat.None) return from > after ? from : null;
        if (from > after) return from;

        var stepDays = repeat switch
        {
            ReminderRepeat.Daily => 1,
            ReminderRepeat.Weekly => 7,
            ReminderRepeat.EveryTwoWeeks => 14,
            _ => 0,
        };

        if (stepDays > 0)
        {
            var step = TimeSpan.FromDays(stepDays);
            var skip = Math.Max(1, (int)Math.Ceiling((after - from).TotalDays / stepDays));
            var candidate = from.AddTicks(step.Ticks * skip);

            // Rounding can land exactly on `after`; the contract is "strictly after".
            for (var guard = 0; candidate <= after && guard < MaxAdvanceSteps; guard++)
                candidate = candidate.AddTicks(step.Ticks);

            return candidate;
        }

        // Monthly: always measured from the original day-of-month.
        var months = 1;
        var monthly = from.AddMonths(months);
        while (monthly <= after && months < MaxAdvanceSteps)
        {
            months++;
            monthly = from.AddMonths(months);
        }

        return monthly;
    }

    /// <summary>The next time this reminder should fire, given that <paramref name="now"/> has passed.</summary>
    public static DateTime? NextAfter(Reminder reminder, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return NextOccurrence(reminder.Repeat, reminder.DueAt, now);
    }

    public static bool IsDue(Reminder reminder, DateTime now)
    {
        if (reminder is null || !reminder.Enabled) return false;
        if (reminder.DueAt > now) return false;

        return reminder.LastFiredAt is null || reminder.LastFiredAt.Value < reminder.DueAt;
    }

    /// <summary>
    /// How many of this reminder's times have passed unseen, counting the current one. Used only to
    /// say so in the notification — a phone that was off for three days should not post three
    /// notifications at once.
    /// </summary>
    public static int MissedOccurrences(Reminder reminder, DateTime now, int cap = 100)
    {
        if (reminder is null || reminder.DueAt > now) return 0;
        if (reminder.Repeat == ReminderRepeat.None) return 1;

        var count = 1;
        var cursor = reminder.DueAt;

        while (count < cap)
        {
            var next = NextOccurrence(reminder.Repeat, cursor, cursor);
            if (next is null || next.Value > now) break;

            cursor = next.Value;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Moves the reminder past <paramref name="firedAt"/>. Returns whether it is still enabled
    /// afterwards: a one-shot reminder turns itself off once it has been delivered.
    /// </summary>
    public static bool Advance(Reminder reminder, DateTime firedAt)
    {
        ArgumentNullException.ThrowIfNull(reminder);

        reminder.FireCount++;
        reminder.LastFiredAt = firedAt;

        if (reminder.Repeat == ReminderRepeat.None)
        {
            reminder.Enabled = false;
            return false;
        }

        var next = NextOccurrence(reminder.Repeat, reminder.DueAt, firedAt);
        if (next is null)
        {
            reminder.Enabled = false;
            return false;
        }

        reminder.DueAt = next.Value;
        return reminder.Enabled;
    }

    public static string DescribeRepeat(ReminderRepeat repeat) => repeat switch
    {
        ReminderRepeat.Daily => "每天",
        ReminderRepeat.Weekly => "每周",
        ReminderRepeat.Monthly => "每月",
        ReminderRepeat.EveryTwoWeeks => "每两周",
        _ => "一次",
    };

    public static ReminderRepeat? ParseRepeat(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();

        return value switch
        {
            "" or "none" or "once" or "一次" or "不重复" => ReminderRepeat.None,
            "daily" or "day" or "每天" or "每日" => ReminderRepeat.Daily,
            "weekly" or "week" or "每周" => ReminderRepeat.Weekly,
            "monthly" or "month" or "每月" => ReminderRepeat.Monthly,
            "biweekly" or "fortnightly" or "everytwoweeks" or "每两周" or "两周一次" => ReminderRepeat.EveryTwoWeeks,
            _ => null,
        };
    }

    /// <summary>Human wording for a due time, relative to today where that is clearer.</summary>
    public static string DescribeWhen(DateTime due, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var day = due.Date;

        if (day == today) return "今天 " + due.ToString("HH:mm");
        if (day == today.AddDays(1)) return "明天 " + due.ToString("HH:mm");
        if (day == today.AddDays(2)) return "后天 " + due.ToString("HH:mm");
        if (day == today.AddDays(-1)) return "昨天 " + due.ToString("HH:mm");

        return due.Year == today.Year ? due.ToString("MM-dd HH:mm") : due.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// Accepts an absolute local time, or a relative offset such as <c>+30m</c> / <c>+2h</c> /
    /// <c>+1d</c>. The relative forms exist because models are unreliable at date arithmetic and
    /// accepting them costs a dozen lines.
    /// </summary>
    public static DateTime? ParseDue(string? text, DateTime now)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;

        if (value[0] is '+' or '＋') return ParseOffset(value[1..], now);

        if (DateTime.TryParseExact(value, FullFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var full))
            return DateTime.SpecifyKind(full, DateTimeKind.Unspecified);

        // "11-15 09:30" has no year: use this year, or next year if that time has already passed.
        if (DateTime.TryParseExact(value, ShortDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var shortDate))
        {
            var candidate = new DateTime(now.Year, shortDate.Month, shortDate.Day,
                shortDate.Hour, shortDate.Minute, 0, DateTimeKind.Unspecified);
            return candidate < now ? candidate.AddYears(1) : candidate;
        }

        // "09:30" alone: today if it is still ahead, otherwise tomorrow.
        if (DateTime.TryParseExact(value, TimeOnlyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var timeOnly))
        {
            var candidate = now.Date.AddHours(timeOnly.Hour).AddMinutes(timeOnly.Minute);
            return candidate <= now ? candidate.AddDays(1) : candidate;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);

        return null;
    }

    private static DateTime? ParseOffset(string body, DateTime now)
    {
        body = body.Trim();
        if (body.Length == 0) return null;

        var unit = char.ToLowerInvariant(body[^1]);
        var digits = unit is 'm' or 'h' or 'd' or 's' ? body[..^1].Trim() : body;
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;

        return unit switch
        {
            's' => now.AddSeconds(amount),
            'h' => now.AddHours(amount),
            'd' => now.AddDays(amount),
            _ => now.AddMinutes(amount), // "+30" reads as minutes
        };
    }
}
