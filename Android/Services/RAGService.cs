using System.IO;
using FairyAI_Android.Models;
using FairyAI_Android.Services.Knowledge;

namespace FairyAI_Android.Services;

/// <summary>
/// Answers questions from the user's own documents.
///
/// Two retrievers, on purpose. Keyword search (idf-weighted term hits) needs no network, no key and no
/// money, and it fails loudly: no shared words, no hit. Vector search finds paraphrases that keyword
/// search cannot, but only when an embedding endpoint is configured. The two lists are merged by rank
/// — see <see cref="RetrievalFusion"/> — and the answer must cite the numbered sources it used.
///
/// This replaces a previous implementation whose "embedding" was a SHA-256 digest read as a 256-dim
/// vector: cosine similarity over hash bytes is noise, so retrieval there was not merely weak, it was
/// meaningless.
/// </summary>
public sealed class RAGService
{
    private const int KeywordCandidates = 8;
    private const int VectorCandidates = 8;
    private const int MaxContextPassages = 4;

    private readonly KnowledgeBaseService _knowledge;
    private readonly ILlmService _llm;
    private readonly EmbeddingClient _embeddings;
    private readonly EmbeddingCache _cache;
    private readonly Func<EmbeddingSettings> _settings;

    public RAGService(
        KnowledgeBaseService knowledge,
        ILlmService llm,
        EmbeddingClient embeddings,
        EmbeddingCache cache,
        Func<EmbeddingSettings> settings)
    {
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Passages currently in the vector cache, for the settings screen.</summary>
    public int CachedPassages => _cache.Count;

    public async Task<RagAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var query = (question ?? "").Trim();
        if (query.Length == 0) return RagAnswer.Fail("没有给出问题");

        if (_knowledge.PassageCount == 0)
            return RagAnswer.Fail("知识库里还没有文档：先到设置里导入要问的资料。");

        var keywordHits = _knowledge.Search(query, KeywordCandidates);
        var vectorHits = await VectorCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
        var hits = RetrievalFusion.Fuse(keywordHits, vectorHits, MaxContextPassages);

        if (hits.Count == 0)
            return RagAnswer.Fail(RagPrompt.NoContextAnswer);

        var prompt = RagPrompt.Build(query, hits);
        var answer = new System.Text.StringBuilder();

        try
        {
            await foreach (var chunk in _llm
                .StreamChatAsync(new List<ChatMessage>(), prompt)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
                answer.Append(chunk);
        }
        catch (OperationCanceledException)
        {
            return RagAnswer.Fail("这个问题被取消了");
        }
        catch (Exception ex)
        {
            return RagAnswer.Fail($"回答没能生成：{ex.GetType().Name}");
        }

        var text = answer.ToString().Trim();
        if (text.Length == 0) return RagAnswer.Fail("模型没有给出回答");

        // The streaming API reports some failures as ordinary text; those must not be passed off as an
        // answer about the user's documents.
        if (Compression.ContextWindow.LooksLikeFailure(text))
            return RagAnswer.Fail(text);

        return new RagAnswer(
            true,
            text,
            RagPrompt.Sources(hits),
            RagPrompt.DescribeSources(hits),
            null);
    }

    /// <summary>
    /// Vector candidates for the question, or none when embeddings are not configured. A failure here
    /// does not fail the question: keyword retrieval still works, and the answer says which retriever
    /// produced it.
    /// </summary>
    private async Task<IReadOnlyList<(string Document, string Passage, double Similarity)>> VectorCandidatesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (!settings.IsUsable) return Array.Empty<(string, string, double)>();

        var passages = _knowledge.Passages;
        if (passages.Count == 0) return Array.Empty<(string, string, double)>();

        _cache.PruneToModel(settings.Model);

        // Only the passages without a cached vector are embedded; this is what keeps a question from
        // costing money every time the app is opened.
        var missing = new List<(string Key, string Document, string Text)>();
        foreach (var (document, text) in passages)
        {
            var key = EmbeddingCache.Key(settings.Model, document, text);
            if (_cache.Get(key) is null) missing.Add((key, document, text));
        }

        if (missing.Count > 0)
        {
            var batch = await _embeddings
                .EmbedAsync(settings, missing.Select(item => item.Text).ToList(), cancellationToken)
                .ConfigureAwait(false);

            if (!batch.Ok) return Array.Empty<(string, string, double)>();

            for (var index = 0; index < missing.Count && index < batch.Vectors.Count; index++)
                _cache.Put(missing[index].Key, batch.Vectors[index]);

            _cache.Save();
        }

        var queryBatch = await _embeddings.EmbedAsync(settings, new[] { query }, cancellationToken)
            .ConfigureAwait(false);

        if (!queryBatch.Ok || queryBatch.Vectors.Count == 0)
            return Array.Empty<(string, string, double)>();

        var queryVector = queryBatch.Vectors[0];
        var scored = new List<(string, string, double)>();

        foreach (var (document, passage) in passages)
        {
            var vector = _cache.Get(EmbeddingCache.Key(settings.Model, document, passage));
            if (vector is null) continue;

            var similarity = VectorMath.Cosine(queryVector, vector);
            if (similarity > 0) scored.Add((document, passage, similarity));
        }

        return scored
            .OrderByDescending(item => item.Item3)
            .Take(VectorCandidates)
            .ToList();
    }
}
