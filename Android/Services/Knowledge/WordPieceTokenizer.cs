using System.Globalization;
using System.Text;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>Token ids and the attention mask for one text, as the model expects them.</summary>
public sealed record TokenizedText(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds)
{
    public int Length => InputIds.Length;
}

/// <summary>
/// BERT-style WordPiece tokenisation, enough to run the bundled Chinese embedding model.
///
/// Hand-written rather than pulled in as a dependency: the format is small (a vocabulary file, one
/// token per line) and the rules are published. The parts that matter for Chinese are that every CJK
/// character is its own token, and that unknown characters become [UNK] instead of being dropped —
/// dropping them silently changes the sentence's vector.
/// </summary>
public sealed class WordPieceTokenizer
{
    public const string UnknownToken = "[UNK]";
    public const string ClassifyToken = "[CLS]";
    public const string SeparatorToken = "[SEP]";
    public const int DefaultMaxLength = 256;

    private readonly Dictionary<string, int> _vocabulary;
    private readonly int _unknownId;
    private readonly int _classifyId;
    private readonly int _separatorId;

    private WordPieceTokenizer(Dictionary<string, int> vocabulary)
    {
        _vocabulary = vocabulary;
        _unknownId = vocabulary.TryGetValue(UnknownToken, out var unknown) ? unknown : 100;
        _classifyId = vocabulary.TryGetValue(ClassifyToken, out var classify) ? classify : 101;
        _separatorId = vocabulary.TryGetValue(SeparatorToken, out var separator) ? separator : 102;
    }

    public int VocabularySize => _vocabulary.Count;

    public static WordPieceTokenizer FromVocabulary(IEnumerable<string> lines)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;

        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            var token = (line ?? "").TrimEnd('\r', '\n');
            if (token.Length == 0 && index > 0) continue;
            vocabulary[token] = index++;
        }

        return new WordPieceTokenizer(vocabulary);
    }

    public TokenizedText Tokenize(string? text, int maxLength = DefaultMaxLength)
    {
        var ids = new List<long> { _classifyId };

        foreach (var word in SplitWords(text))
        {
            if (ids.Count >= maxLength - 1) break;

            foreach (var id in WordPieces(word))
            {
                ids.Add(id);
                if (ids.Count >= maxLength - 1) break;
            }
        }

        ids.Add(_separatorId);

        return new TokenizedText(ids.ToArray(), Enumerable.Repeat(1L, ids.Count).ToArray(), new long[ids.Count]);
    }

    /// <summary>Lower-case, strip accents, split on whitespace and punctuation; CJK one character each.</summary>
    internal static IEnumerable<string> SplitWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var builder = new StringBuilder();

        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;

            if (IsCjk(character))
            {
                if (builder.Length > 0) { yield return builder.ToString(); builder.Clear(); }
                yield return character.ToString();
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0) { yield return builder.ToString(); builder.Clear(); }
                continue;
            }

            if (char.IsPunctuation(character) || char.IsSymbol(character))
            {
                if (builder.Length > 0) { yield return builder.ToString(); builder.Clear(); }
                yield return character.ToString();
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        if (builder.Length > 0) yield return builder.ToString();
    }

    /// <summary>Greedy longest-match; continuation pieces are prefixed with <c>##</c>.</summary>
    private IEnumerable<long> WordPieces(string word)
    {
        if (word.Length == 0) yield break;

        if (_vocabulary.TryGetValue(word, out var whole))
        {
            yield return whole;
            yield break;
        }

        var start = 0;
        var pieces = new List<long>();

        while (start < word.Length)
        {
            var end = word.Length;
            var matched = -1;

            while (start < end)
            {
                var candidate = start > 0 ? "##" + word[start..end] : word[start..end];
                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    matched = id;
                    break;
                }

                end--;
            }

            if (matched < 0)
            {
                // One unknown piece makes the whole word unknown, which is what the reference
                // implementation does; a partially matched word would tokenise differently.
                yield return _unknownId;
                yield break;
            }

            pieces.Add(matched);
            start = end;
        }

        foreach (var piece in pieces) yield return piece;
    }

    internal static bool IsCjk(char character) =>
        character is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF';
}

/// <summary>
/// Turns token-level model output into one vector: mean over the real tokens (attention mask), then
/// L2 normalisation so cosine similarity is a dot product.
/// </summary>
public static class TokenPooling
{
    /// <summary><paramref name="hidden"/> is [tokens, dimension] laid out row by row.</summary>
    public static float[] MeanPool(float[]? hidden, long[]? attentionMask, int tokenCount, int dimension)
    {
        if (hidden is null || dimension <= 0 || tokenCount <= 0) return Array.Empty<float>();

        var pooled = new float[dimension];
        var counted = 0;

        for (var token = 0; token < tokenCount; token++)
        {
            if (attentionMask is not null && token < attentionMask.Length && attentionMask[token] == 0) continue;

            var offset = token * dimension;
            if (offset + dimension > hidden.Length) break;

            for (var i = 0; i < dimension; i++) pooled[i] += hidden[offset + i];
            counted++;
        }

        if (counted == 0) return Array.Empty<float>();

        for (var i = 0; i < dimension; i++) pooled[i] /= counted;
        return Normalize(pooled);
    }

    public static float[] Normalize(float[]? vector)
    {
        if (vector is null || vector.Length == 0) return Array.Empty<float>();

        double sum = 0;
        foreach (var value in vector) sum += value * (double)value;

        var length = Math.Sqrt(sum);
        if (length <= 1e-12) return vector;

        var normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) normalized[i] = (float)(vector[i] / length);
        return normalized;
    }
}
