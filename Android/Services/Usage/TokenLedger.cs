using System.Text.Json;

namespace FairyAI_Android.Services.Usage;

/// <summary>One recorded call. <paramref name="Exact"/> is false when the number was estimated.</summary>
public sealed record TokenEntry(DateOnly Day, int Prompt, int Completion, bool Exact);

public sealed record TokenTotals(int Prompt, int Completion, int Calls)
{
    public int Total => Prompt + Completion;

    public static readonly TokenTotals Empty = new(0, 0, 0);
}

/// <summary>
/// Keeps the token counts the app has actually been told about. Pure: no files, no clock beyond the day
/// passed in, so the arithmetic and the JSON round-trip are testable.
///
/// Only numbers that came from a provider's <c>usage</c> block are recorded as exact. A count that was
/// guessed is marked as a guess — a fake precise number would be worse than a labelled estimate.
/// </summary>
public sealed class TokenLedger
{
    private const int MaxEntries = 2000;

    private readonly List<TokenEntry> _entries;

    public TokenLedger(IEnumerable<TokenEntry>? entries = null) =>
        _entries = entries?.ToList() ?? new List<TokenEntry>();

    public IReadOnlyList<TokenEntry> Entries => _entries;

    public void Add(TokenEntry entry)
    {
        if (entry.Prompt < 0 || entry.Completion < 0) return;

        _entries.Add(entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    public void Clear() => _entries.Clear();

    public TokenTotals Totals() => Sum(_entries);

    public TokenTotals Since(DateOnly day) => Sum(_entries.Where(entry => entry.Day >= day));

    public TokenTotals ExactTotals() => Sum(_entries.Where(entry => entry.Exact));

    public TokenTotals EstimatedTotals() => Sum(_entries.Where(entry => !entry.Exact));

    private static TokenTotals Sum(IEnumerable<TokenEntry> entries)
    {
        var prompt = 0;
        var completion = 0;
        var calls = 0;

        foreach (var entry in entries)
        {
            prompt += entry.Prompt;
            completion += entry.Completion;
            calls++;
        }

        return new TokenTotals(prompt, completion, calls);
    }

    /// <summary>
    /// Rough count for text nobody reported numbers for: about four characters per token for latin text,
    /// one per CJK character. Used only for entries recorded as estimates.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        var other = 0;

        foreach (var character in text)
        {
            if (character >= '\u4e00' && character <= '\u9fff') cjk++;
            else other++;
        }

        return cjk + (int)Math.Ceiling(other / 4.0);
    }

    public string ToJson() => JsonSerializer.Serialize(_entries);

    public static TokenLedger FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TokenLedger();

        try
        {
            var entries = JsonSerializer.Deserialize<List<TokenEntry>>(json);
            return new TokenLedger(entries);
        }
        catch (JsonException)
        {
            // A damaged file must not take the feature down; the counts simply start again.
            return new TokenLedger();
        }
    }
}
