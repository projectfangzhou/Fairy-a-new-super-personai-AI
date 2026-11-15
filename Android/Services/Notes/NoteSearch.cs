namespace FairyAI_Android.Services.Notes;

/// <summary>
/// Keyword search over notes. Deliberately dependency-free and deterministic: every query term has
/// to appear somewhere (title, tag or body), which keeps results explainable — a user who searches
/// two words and gets a note containing only one of them would rightly call that broken.
///
/// Scoring only decides order: title hits beat tag hits, tag hits beat body hits, and a note pinned
/// by the user wins ties.
/// </summary>
public static class NoteSearch
{
    private const int TitleWeight = 6;
    private const int TagWeight = 4;
    private const int BodyWeight = 2;
    private const int SnippetWidth = 50;

    public static List<NoteHit> Search(IEnumerable<Note> notes, string? query, int limit = 20)
    {
        var terms = SplitTerms(query);
        var hits = new List<NoteHit>();

        foreach (var note in notes ?? Enumerable.Empty<Note>())
        {
            if (note is null) continue;

            if (terms.Count == 0)
            {
                hits.Add(new NoteHit(note, 0, Snippet(note, null)));
                continue;
            }

            var score = Score(note, terms);
            if (score <= 0) continue;

            hits.Add(new NoteHit(note, score, Snippet(note, terms)));
        }

        hits.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0) return byScore;

            var byPin = right.Note.Pinned.CompareTo(left.Note.Pinned);
            if (byPin != 0) return byPin;

            return right.Note.UpdatedAt.CompareTo(left.Note.UpdatedAt);
        });

        return limit > 0 && hits.Count > limit ? hits.GetRange(0, limit) : hits;
    }

    /// <summary>Every term must match; the score is the sum of what each one matched.</summary>
    public static int Score(Note note, IReadOnlyList<string> terms)
    {
        if (note is null || terms.Count == 0) return 0;

        var score = 0;

        foreach (var term in terms)
        {
            var matched = 0;

            if (Contains(note.Title, term)) matched += TitleWeight;

            foreach (var tag in note.Tags ?? new List<string>())
            {
                if (!Contains(tag, term)) continue;
                matched += TagWeight;
                break;
            }

            if (Contains(note.Body, term)) matched += BodyWeight;

            if (matched == 0) return 0;
            score += matched;
        }

        return score;
    }

    /// <summary>Where the first term first appears, widened by a little context.</summary>
    public static string Snippet(Note note, IReadOnlyList<string>? terms)
    {
        var body = (note.Body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        if (body.Length == 0) return "";
        if (terms is null || terms.Count == 0) return Clip(body, 0, 0);

        var index = -1;
        foreach (var term in terms)
        {
            var found = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (index < 0 || found < index)) index = found;
            if (index == 0) break;
        }

        return index < 0 ? Clip(body, 0, 0) : Clip(body, index, terms[0].Length);
    }

    /// <summary>Terms by whitespace, deduplicated case-insensitively. Empty query means "everything".</summary>
    public static List<string> SplitTerms(string? query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return terms;

        foreach (var raw in query.Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '、', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim();
            if (term.Length == 0) continue;
            if (terms.Any(existing => string.Equals(existing, term, StringComparison.OrdinalIgnoreCase))) continue;
            terms.Add(term);
        }

        return terms;
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text, int matchIndex, int matchLength)
    {
        if (text.Length <= SnippetWidth * 2) return text;

        var start = Math.Max(0, matchIndex - SnippetWidth);
        var end = Math.Min(text.Length, Math.Max(start, matchIndex + matchLength) + SnippetWidth);

        var slice = text[start..end].Trim();
        return (start > 0 ? "…" : "") + slice + (end < text.Length ? "…" : "");
    }
}
