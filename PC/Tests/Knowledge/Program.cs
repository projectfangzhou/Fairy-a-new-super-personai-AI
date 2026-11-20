using MyAiAssistant.Services.Knowledge;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

void Skip(string name) => Console.WriteLine("SKIP: " + name);

// ------------------------------------------------------------------ pooling

var hidden = new float[]
{
    1, 0, 0,     // first token
    0, 5, 0,     // second token
    9, 9, 9,     // padding — must never be pooled
};

var pooled = QwenPooling.LastToken(hidden, new long[] { 1, 1, 0 }, tokenCount: 3, dimension: 3);
Assert(pooled.Length == 3, "pooling keeps the model dimension");
Assert(Math.Abs(pooled[0]) < 1e-6 && Math.Abs(pooled[1] - 1f) < 1e-6,
    "the last real token is pooled, not the padding row");
Assert(Math.Abs(pooled[2]) < 1e-6, "other dimensions stay zero");

var normalised = QwenPooling.Normalize(new[] { 3f, 4f });
Assert(Math.Abs(normalised[0] - 0.6f) < 1e-6 && Math.Abs(normalised[1] - 0.8f) < 1e-6,
    "the pooled vector is normalised to unit length");

Assert(QwenPooling.LastToken(null, null, 1, 4).Length == 0, "missing hidden state yields nothing");
Assert(QwenPooling.LastToken(hidden, new long[] { 0, 0, 0 }, 3, 3).Length == 0,
    "a fully masked sequence yields nothing instead of the padding row");
Assert(QwenPooling.Normalize(new[] { 0f, 0f }).SequenceEqual(new[] { 0f, 0f }),
    "a zero vector is left alone instead of dividing by zero");

// ------------------------------------------------------------------ tokenizer

// Walk up from the test binary to the repository root, wherever the build put us.
static string? FindModelDirectory()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    for (var depth = 0; depth < 8 && directory is not null; depth++)
    {
        var candidate = Path.Combine(directory.FullName, "Dependencies", "qwen3-embedding");
        if (Directory.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }

    return null;
}

var modelDirectory = FindModelDirectory();
var vocabularyPath = modelDirectory is null ? "" : Path.Combine(modelDirectory, "vocab.json");
var mergesPath = modelDirectory is null ? "" : Path.Combine(modelDirectory, "merges.txt");

var addedTokensPath = modelDirectory is null ? "" : Path.Combine(modelDirectory, "added_tokens.json");

if (!File.Exists(vocabularyPath) || !File.Exists(mergesPath))
{
    Skip("tokenizer tests need Dependencies/qwen3-embedding/{vocab,merges}.txt");
}
else
{
    var tokenizer = BpeTokenizer.Load(vocabularyPath, mergesPath, File.Exists(addedTokensPath) ? addedTokensPath : null);
    Assert(tokenizer.VocabularySize > 100_000, "the Qwen vocabulary loads");

    var encoded = tokenizer.Encode("预算在本季度增加了百分之十五");
    Assert(encoded.Length > 3, "a Chinese sentence becomes several tokens");
    Assert(encoded.AttentionMask.All(flag => flag == 1), "every token is attended to");
    Assert(encoded.InputIds.All(id => id >= 0 && id < tokenizer.VocabularySize), "all ids are inside the vocabulary");
    Assert(encoded.InputIds[^1] == 151643, "the sequence ends with the end-of-text token");

    var again = tokenizer.Encode("预算在本季度增加了百分之十五");
    Assert(again.InputIds.SequenceEqual(encoded.InputIds), "tokenisation is deterministic");

    var other = tokenizer.Encode("明天下午三点在三楼会议室开会");
    Assert(!other.InputIds.SequenceEqual(encoded.InputIds), "different text tokenises differently");
    Assert(other.InputIds[^1] == 151643, "the end-of-text token is added every time");

    var latin = tokenizer.Encode("the quarterly budget increased by 15%");
    Assert(latin.Length > 3, "latin text tokenises too");

    var empty = tokenizer.Encode("");
    Assert(empty.Length == 1 && empty.InputIds[0] == 151643, "empty text degrades to a single end token");

    var withoutSuffix = tokenizer.Encode("预算", suffixToken: null);
    Assert(!withoutSuffix.InputIds.Contains(151643L) || withoutSuffix.Length == 1,
        "the suffix can be left out");

    var capped = tokenizer.Encode(string.Concat(Enumerable.Repeat("预算管理", 400)), maxLength: 32);
    Assert(capped.Length <= 32, "the token limit is respected");
    Assert(capped.InputIds[^1] == 151643, "a capped sequence still ends with the end token");

    // Subword behaviour: a word the vocabulary knows should come back as fewer tokens than its bytes.
    var whole = tokenizer.Encode("budget", suffixToken: null);
    Assert(whole.Length <= 3, "a common English word merges into a few tokens");

    Console.WriteLine($"  (\"预算在本季度增加了百分之十五\" -> {encoded.Length} tokens)");
}

// ------------------------------------------------------------------ the model file

var missing = new QwenEmbeddingService(Path.Combine(Path.GetTempPath(), "no-model-here"));
Assert(!missing.IsAvailable, "without a model file the embedder is unavailable");
Assert(missing.UnavailableReason is { Length: > 0 } && missing.UnavailableReason!.Contains("没有找到模型文件", StringComparison.Ordinal),
    "the reason names the missing model file");

var emptyVector = await missing.EmbedDocumentAsync("预算");
Assert(emptyVector.Length == 0, "an unavailable embedder returns no vector instead of zeros");

// ------------------------------------------------------------------ the real model

static double Cosine(float[] a, float[] b)
{
    if (a.Length == 0 || a.Length != b.Length) return 0;

    double dot = 0, na = 0, nb = 0;
    for (var i = 0; i < a.Length; i++)
    {
        dot += a[i] * (double)b[i];
        na += a[i] * (double)a[i];
        nb += b[i] * (double)b[i];
    }

    var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
    return denominator <= 1e-12 ? 0 : dot / denominator;
}

var modelPath = modelDirectory is null ? "" : Path.Combine(modelDirectory, "model_quantized.onnx");

if (!File.Exists(modelPath))
{
    Skip("the semantic check needs model_quantized.onnx (585 MB)");
}
else
{
    using var embedder = new QwenEmbeddingService(modelDirectory);

    Assert(embedder.IsAvailable, "the 0.6B model loads: " + (embedder.UnavailableReason ?? "ok"));

    var budget = await embedder.EmbedDocumentAsync("预算在本季度增加了百分之十五");
    var raise = await embedder.EmbedDocumentAsync("这个季度市场预算上调了 15%");
    var meeting = await embedder.EmbedDocumentAsync("明天下午三点在三楼会议室开会");

    Assert(budget.Length > 0, "a document embeds to a vector");
    Assert(budget.Length == raise.Length, "all documents share one dimension");
    Assert(Math.Abs(Math.Sqrt(budget.Select(value => value * (double)value).Sum()) - 1) < 1e-3,
        "vectors are unit length");

    var related = Cosine(budget, raise);
    var unrelated = Cosine(budget, meeting);

    Console.WriteLine($"  dimension {budget.Length}, embed time {embedder.LastEmbedMilliseconds} ms");
    Console.WriteLine($"  同义句 {related:0.000} / 无关句 {unrelated:0.000}");

    Assert(related > unrelated, "two paraphrases are closer than an unrelated sentence");
    Assert(related > 0.7, "paraphrases score high enough to be useful for retrieval");

    // A query uses the instruction prefix; retrieval should still rank the matching passage first.
    var query = await embedder.EmbedQueryAsync("市场预算这个季度怎么变的");
    Assert(Cosine(query, budget) > Cosine(query, meeting), "a question retrieves the passage that answers it");
}

Console.WriteLine();
Console.WriteLine($"{checks} checks passed");
