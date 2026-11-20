using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyAiAssistant.Services.Knowledge;

/// <summary>Token ids for one text, as the ONNX model expects them.</summary>
public sealed record EncodedText(long[] InputIds, long[] AttentionMask)
{
    public int Length => InputIds.Length;
}

/// <summary>
/// Byte-level BPE, the tokenizer Qwen-family models use (the same scheme GPT-2 introduced).
///
/// Written out rather than pulled in: it is a vocabulary file, a merge list and published rules, and a
/// tokenizer that is subtly wrong produces vectors that look plausible and retrieve badly — which is
/// the worst kind of bug to inherit from a dependency you cannot read.
///
/// Three details decide correctness:
/// <list type="bullet">
/// <item>text is first turned into UTF-8 bytes and then mapped through the byte→character table, so
/// every possible input, including emoji and rare CJK, has a token representation;</item>
/// <item>splitting happens before merging, on the GPT-4 pattern, so "budget" and " budget" are
/// different pieces;</item>
/// <item>merges are applied lowest-rank first, which is what makes the result match the reference.</item>
/// </list>
/// </summary>
public sealed class BpeTokenizer
{
    /// <summary>The pre-tokenisation pattern Qwen and GPT-4 share.</summary>
    private static readonly Regex SplitPattern = new(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled);

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string Left, string Right), int> _ranks;
    private readonly Dictionary<char, byte> _characterToByte;

    private BpeTokenizer(Dictionary<string, int> vocabulary, Dictionary<(string, string), int> ranks)
    {
        _vocabulary = vocabulary;
        _ranks = ranks;
        _characterToByte = BuildCharacterToByte();
    }

    public int VocabularySize => _vocabulary.Count;

    /// <summary>
    /// Loads the tokenizer. <paramref name="addedTokensPath"/> matters: the Qwen vocabulary stops at
    /// 151,642 entries and every special token (<c>&lt;|endoftext|&gt;</c>, <c>&lt;|im_end|&gt;</c>, …) lives
    /// in <c>added_tokens.json</c> instead. Without it the end-of-sequence token silently cannot be
    /// encoded, which shifts what the model pools on.
    /// </summary>
    public static BpeTokenizer Load(string vocabularyPath, string mergesPath, string? addedTokensPath = null)
    {
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabularyPath))
                         ?? new Dictionary<string, int>();

        if (!string.IsNullOrEmpty(addedTokensPath) && File.Exists(addedTokensPath))
        {
            var added = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(addedTokensPath))
                        ?? new Dictionary<string, int>();

            foreach (var pair in added) vocabulary[pair.Key] = pair.Value;
        }

        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;

        foreach (var line in File.ReadLines(mergesPath))
        {
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            var space = line.IndexOf(' ');
            if (space <= 0 || space >= line.Length - 1) continue;

            ranks[(line[..space], line[(space + 1)..])] = rank++;
        }

        return new BpeTokenizer(vocabulary, ranks);
    }

    /// <summary>
    /// Encodes <paramref name="text"/>. <paramref name="suffixToken"/> is appended when given — Qwen3
    /// embeddings pool on the final token, so a document without its end token pools on the wrong one.
    /// </summary>
    public EncodedText Encode(string? text, string? suffixToken = "<|endoftext|>", int maxLength = 512)
    {
        var ids = new List<long>();

        foreach (Match match in SplitPattern.Matches(text ?? ""))
        {
            foreach (var id in EncodePiece(match.Value))
            {
                ids.Add(id);
                if (ids.Count >= maxLength - 1) break;
            }

            if (ids.Count >= maxLength - 1) break;
        }

        if (!string.IsNullOrEmpty(suffixToken) && _vocabulary.TryGetValue(suffixToken!, out var suffix) &&
            ids.Count < maxLength)
            ids.Add(suffix);

        if (ids.Count == 0) ids.Add(_vocabulary.TryGetValue("<|endoftext|>", out var fallback) ? fallback : 0);

        return new EncodedText(ids.ToArray(), Enumerable.Repeat(1L, ids.Count).ToArray());
    }

    private IEnumerable<long> EncodePiece(string piece)
    {
        if (piece.Length == 0) yield break;

        // Bytes → the printable characters the vocabulary is written in.
        var builder = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(piece))
            builder.Append(FromByte(value));

        var symbols = builder.ToString().Select(character => character.ToString()).ToList();

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;

            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (!_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank)) continue;
                if (rank >= bestRank) continue;

                bestRank = rank;
                bestIndex = i;
            }

            if (bestIndex < 0) break;

            symbols[bestIndex] = symbols[bestIndex] + symbols[bestIndex + 1];
            symbols.RemoveAt(bestIndex + 1);
        }

        foreach (var symbol in symbols)
        {
            if (_vocabulary.TryGetValue(symbol, out var id)) yield return id;
        }
    }

    /// <summary>The GPT-2 byte↔character mapping: printable stand-ins for bytes that are not.</summary>
    private static Dictionary<char, byte> BuildCharacterToByte()
    {
        var map = new Dictionary<char, byte>();
        var printable = new List<int>();

        for (var b = (int)'!'; b <= (int)'~'; b++) printable.Add(b);
        for (var b = 0xA1; b <= 0xAC; b++) printable.Add(b);
        for (var b = 0xAE; b <= 0xFF; b++) printable.Add(b);

        var mapped = printable.Select(value => (byte)value).ToList();
        var next = 0;

        for (var value = 0; value < 256; value++)
        {
            if (mapped.Contains((byte)value))
            {
                map[(char)value] = (byte)value;
                continue;
            }

            mapped.Add((byte)value);
            map[(char)(256 + next)] = (byte)value;
            next++;
        }

        return map;
    }

    private static readonly Dictionary<byte, char> ByteToCharacter = BuildByteToCharacter();

    private static Dictionary<byte, char> BuildByteToCharacter()
    {
        var map = new Dictionary<byte, char>();
        foreach (var pair in BuildCharacterToByte()) map[pair.Value] = pair.Key;
        return map;
    }

    private static char FromByte(byte value) => ByteToCharacter.TryGetValue(value, out var character) ? character : '?';
}

/// <summary>
/// Pools a token-level model output into a single vector. Qwen3 embeddings use the <em>last</em> token,
/// not a mean — pooling the wrong way still yields unit vectors, which is why this is spelled out.
/// </summary>
public static class QwenPooling
{
    public static float[] LastToken(float[]? hidden, long[]? attentionMask, int tokenCount, int dimension)
    {
        if (hidden is null || dimension <= 0 || tokenCount <= 0) return Array.Empty<float>();

        var last = tokenCount - 1;
        if (attentionMask is not null)
        {
            last = -1;
            for (var i = 0; i < tokenCount && i < attentionMask.Length; i++)
                if (attentionMask[i] != 0) last = i;
        }

        if (last < 0) return Array.Empty<float>();

        var vector = new float[dimension];
        var offset = last * dimension;
        if (offset + dimension > hidden.Length) return Array.Empty<float>();

        Array.Copy(hidden, offset, vector, 0, dimension);
        return Normalize(vector);
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
