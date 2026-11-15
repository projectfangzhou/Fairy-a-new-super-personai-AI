namespace FairyAI_Android.Services.Knowledge;

/// <summary>One imported document as the index sees it: a name and its extracted text.</summary>
public sealed record KnowledgeDocument(string Name, string Text);

/// <summary>One matching passage, with the document it came from.</summary>
public sealed record KnowledgeHit(string Document, string Passage, double Score);

/// <summary>
/// What the assistant may ask of the knowledge base. Implemented by the real service, faked in tests.
/// </summary>
public interface IKnowledgeSearch
{
    IReadOnlyList<string> Documents { get; }

    IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 5);
}

/// <summary>
/// Keyword index over the imported documents, built on demand.
///
/// Splitting text into passages matters more than the scoring: a hit is only useful if it comes back
/// as a paragraph the model can quote, not as a whole document. Chinese is tokenised one character at
/// a time (every character is a word-ish unit and there is no separator to trust), Latin text by
/// word — which is why "会议" finds "明天上午的会议安排" without any segmentation library.
/// </summary>
public sealed class KnowledgeIndex
{
    private const int DefaultPassageLength = 400;
    private const int SnippetLength = 160;
    private const int MaxTermsPerQuery = 16;
    private const int MaxCountPerPassage = 3;

    private readonly List<string> _documents = new();
    private readonly List<Passage> _passages = new();
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    private KnowledgeIndex()
    {
    }

    public IReadOnlyList<string> Documents => _documents;

    public int PassageCount => _passages.Count;

    public static KnowledgeIndex Build(IEnumerable<KnowledgeDocument> documents, int passageLength = DefaultPassageLength)
    {
        var index = new KnowledgeIndex();
        var length = Math.Max(80, passageLength);

        foreach (var document in documents ?? Enumerable.Empty<KnowledgeDocument>())
        {
            if (document is null || string.IsNullOrWhiteSpace(document.Text)) continue;

            var name = string.IsNullOrWhiteSpace(document.Name) ? "(未命名)" : document.Name.Trim();
            index._documents.Add(name);

            var nameTokens = Tokenize(name).ToHashSet(StringComparer.Ordinal);

            foreach (var passageText in Split(document.Text, length))
            {
                var tokens = Tokenize(passageText);
                if (tokens.Count == 0) continue;

                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var token in tokens)
                    counts[token] = counts.TryGetValue(token, out var existing) ? existing + 1 : 1;

                index._passages.Add(new Passage(name, passageText, counts, nameTokens));

                foreach (var token in counts.Keys)
                    index._documentFrequency[token] = index._documentFrequency.TryGetValue(token, out var df) ? df + 1 : 1;
            }
        }

        return index;
    }

    public IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 5)
    {
        var terms = Tokenize(query ?? "").Distinct(StringComparer.Ordinal).Take(MaxTermsPerQuery).ToList();
        if (terms.Count == 0 || _passages.Count == 0) return Array.Empty<KnowledgeHit>();

        var total = _passages.Count;

        // A long query has to share more than one term with the passage. Without this, "量子计算"
        // would hit any text containing 算, because Chinese is tokenised character by character.
        var requiredMatches = terms.Count >= 3 ? (int)Math.Ceiling(terms.Count / 2.0) : 1;

        var scored = new List<KnowledgeHit>();

        foreach (var passage in _passages)
        {
            double score = 0;
            var matchedTerms = 0;

            foreach (var term in terms)
            {
                if (!passage.Counts.TryGetValue(term, out var count)) continue;

                matchedTerms++;
                var idf = Math.Log(1 + (double)total / (1 + DocumentFrequency(term)));
                score += idf * Math.Min(count, MaxCountPerPassage);
                if (passage.NameTokens.Contains(term)) score += idf * 2;
            }

            if (matchedTerms < requiredMatches) continue;

            scored.Add(new KnowledgeHit(passage.Document, Clip(passage.Text, terms), score));
        }

        scored.Sort((left, right) => right.Score.CompareTo(left.Score));

        // Two passages from the same document are rarely both useful; keep the better one.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<KnowledgeHit>();
        foreach (var hit in scored)
        {
            if (!seen.Add(hit.Document)) continue;

            hits.Add(hit);
            if (hits.Count >= Math.Max(1, limit)) break;
        }

        return hits;
    }

    /// <summary>CJK characters one at a time, Latin/digit runs as words, everything else a separator.</summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            tokens.Add(buffer.ToString().ToLowerInvariant());
            buffer.Clear();
        }

        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                Flush();
                tokens.Add(character.ToString());
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                buffer.Append(character);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;
    }

    private int DocumentFrequency(string term) =>
        _documentFrequency.TryGetValue(term, out var df) ? df : 0;

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private static IEnumerable<string> Split(string text, int length)
    {
        using var reader = new StringReader(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        var buffer = new System.Text.StringBuilder();

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString().Trim();
                    buffer.Clear();
                }

                continue;
            }

            if (buffer.Length + trimmed.Length + 1 > length && buffer.Length > 0)
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
            }

            // A single very long line is cut where it exceeds the passage length.
            if (trimmed.Length > length)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString().Trim();
                    buffer.Clear();
                }

                var start = 0;
                while (start < trimmed.Length)
                {
                    var take = Math.Min(length, trimmed.Length - start);
                    yield return trimmed.Substring(start, take).Trim();
                    start += take;
                }

                continue;
            }

            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(trimmed);
        }

        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }

    private static string Clip(string passage, List<string> terms)
    {
        var flat = passage.Replace('\n', ' ').Trim();
        if (flat.Length <= SnippetLength) return flat;

        var index = -1;
        foreach (var term in terms)
        {
            var found = flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (index < 0 || found < index)) index = found;
        }

        if (index < 0) return flat[..SnippetLength] + "…";

        var start = Math.Max(0, index - SnippetLength / 3);
        var end = Math.Min(flat.Length, start + SnippetLength);
        var slice = flat[start..end].Trim();

        return (start > 0 ? "…" : "") + slice + (end < flat.Length ? "…" : "");
    }

    private sealed record Passage(string Document, string Text, Dictionary<string, int> Counts, HashSet<string> NameTokens);
}
