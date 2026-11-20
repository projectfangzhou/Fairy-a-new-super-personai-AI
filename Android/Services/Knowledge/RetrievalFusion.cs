namespace FairyAI_Android.Services.Knowledge;

/// <summary>Which retriever(s) produced a hit — reported so "why did it answer that" has an answer.</summary>
public static class RetrievalSource
{
    public const string Keyword = "keyword";
    public const string Vector = "vector";
    public const string Both = "both";
}

/// <summary>One retrieved passage, with the document it came from.</summary>
public sealed record RetrievalHit(string Document, string Passage, double Score, string Source);

/// <summary>
/// Merges two ranked lists — keyword hits and vector hits — into one.
///
/// Reciprocal rank fusion is used instead of comparing scores directly: a keyword score (idf-weighted
/// term hits) and a cosine similarity are not on the same scale, and any attempt to normalise them
/// invents a relationship that does not exist. RRF only uses positions, which is honest: both
/// retrievers agree that their top result is their top result, and a passage both lists rank highly
/// wins on its own merits.
/// </summary>
public static class RetrievalFusion
{
    /// <summary>The usual RRF constant; 60 is the value from the original paper and is not tuned here.</summary>
    public const int RankConstant = 60;

    public static List<RetrievalHit> Fuse(
        IReadOnlyList<KnowledgeHit>? keywordHits,
        IReadOnlyList<(string Document, string Passage, double Similarity)>? vectorHits,
        int limit = 4,
        double minimumSimilarity = 0.2)
    {
        var combined = new Dictionary<string, Entry>(StringComparer.Ordinal);

        void Add(string document, string passage, double score, string source)
        {
            var key = Key(document, passage);
            if (!combined.TryGetValue(key, out var entry))
            {
                entry = new Entry(document, passage);
                combined[key] = entry;
            }

            entry.Score += score;
            entry.Source = entry.Source is null ? source : RetrievalSource.Both;
        }

        var rank = 0;
        foreach (var hit in keywordHits ?? Array.Empty<KnowledgeHit>())
        {
            Add(hit.Document, hit.Passage, 1.0 / (RankConstant + ++rank), RetrievalSource.Keyword);
        }

        rank = 0;
        foreach (var hit in vectorHits ?? Array.Empty<(string, string, double)>())
        {
            // A vector hit below the floor is noise dressed as a result; keyword hits never get this
            // filter because they only exist when the words actually matched.
            if (hit.Similarity < minimumSimilarity) continue;
            Add(hit.Document, hit.Passage, 1.0 / (RankConstant + ++rank), RetrievalSource.Vector);
        }

        return combined.Values
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Max(1, limit))
            .Select(entry => new RetrievalHit(entry.Document, entry.Passage, entry.Score, entry.Source!))
            .ToList();
    }

    private static string Key(string document, string passage) =>
        document + "\u0000" + (passage ?? "").Trim();

    private sealed class Entry
    {
        public Entry(string document, string passage)
        {
            Document = document;
            Passage = passage;
        }

        public string Document { get; }

        public string Passage { get; }

        public double Score { get; set; }

        public string? Source { get; set; }
    }
}
