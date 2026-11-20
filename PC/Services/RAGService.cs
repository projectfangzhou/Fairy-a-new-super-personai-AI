using System.IO;
using System.Linq;
using MyAiAssistant.Models;
using MyAiAssistant.Services.Knowledge;

namespace MyAiAssistant.Services;

/// <summary>
/// Answers questions from the local knowledge folder using the embedding model on this machine.
///
/// This replaces an earlier implementation whose "embedding" was a SHA-256 digest read as a 256-dim
/// vector: cosine similarity over hash bytes is noise, so its "semantic search" returned arbitrary
/// paragraphs. The model here is Qwen3-Embedding-0.6B (int8 ONNX), 1024 dimensions, running locally.
///
/// When the model file is not present the service says so and returns nothing — it does not fall back
/// to pretending, because a wrong answer from a knowledge base is worse than no answer.
/// </summary>
public class RAGService
{
    private readonly VectorDatabase _vectorDb;
    private readonly ILlmService _llm;
    private readonly QwenEmbeddingService _embedder;

    public RAGService(ILlmService llm, QwenEmbeddingService? embedder = null, string knowledgeDir = "")
    {
        _llm = llm;
        var dir = string.IsNullOrEmpty(knowledgeDir)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "knowledge")
            : knowledgeDir;

        Directory.CreateDirectory(dir);
        _vectorDb = new VectorDatabase(Path.Combine(dir, "vectordb.json"));
        _embedder = embedder ?? new QwenEmbeddingService();
    }

    public bool IsReady => _embedder.IsAvailable;

    public string? UnavailableReason => _embedder.UnavailableReason;

    public int IndexedChunks => _vectorDb.Count;

    /// <summary>Embeds and stores one document. Returns how many chunks were indexed.</summary>
    public async Task<(int Chunks, string Message)> IndexDocumentAsync(string filePath)
    {
        if (!_embedder.IsAvailable)
            return (0, "向量模型不可用：" + (_embedder.UnavailableReason ?? "原因未知"));

        if (!File.Exists(filePath))
            return (0, $"找不到文件：{filePath}");

        var text = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return (0, "文件里没有可索引的文字");

        var name = Path.GetFileNameWithoutExtension(filePath);
        var chunks = SplitIntoChunks(text, 400);
        var indexed = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var vector = await _embedder.EmbedDocumentAsync(chunks[i]).ConfigureAwait(false);
            if (vector.Length == 0) continue;

            _vectorDb.Add($"{name}_{i}", vector, chunks[i]);
            indexed++;
        }

        _vectorDb.Save();
        return (indexed, indexed == 0
            ? "没有成功索引任何片段"
            : $"已索引《{name}》的 {indexed} 个片段（{_embedder.LastEmbedMilliseconds} ms/片段）");
    }

    /// <summary>The retrieval half on its own, without calling the model.</summary>
    public async Task<IReadOnlyList<(string Id, string Document, float Score)>> RetrieveAsync(string question, int topK = 4)
    {
        if (!_embedder.IsAvailable || string.IsNullOrWhiteSpace(question))
            return Array.Empty<(string, string, float)>();

        var queryVector = await _embedder.EmbedQueryAsync(question).ConfigureAwait(false);
        if (queryVector.Length == 0) return Array.Empty<(string, string, float)>();

        return _vectorDb.Search(queryVector, topK)
            .Select(hit => (hit.Id, hit.Document, hit.Score))
            .ToList();
    }

    public async Task<string> QueryAsync(string question, int topK = 4)
    {
        if (!_embedder.IsAvailable)
            return "向量模型不可用：" + (_embedder.UnavailableReason ?? "原因未知");

        var results = await RetrieveAsync(question, topK).ConfigureAwait(false);
        if (results.Count == 0) return "知识库中没有找到相关信息。";

        var context = string.Join("\n\n", results.Select(result => result.Document));
        var prompt =
            "只依据下面的资料回答问题。资料里没有的内容，明确说「资料里没有提到」，不要用你自己的知识补全。\n\n" +
            "资料：\n" + context + "\n\n问题：" + question;

        var config = ConfigManager.Load();
        var response = new System.Text.StringBuilder();

        await foreach (var chunk in _llm.StreamChatAsync(
                           new List<ChatMessage>(), prompt, config.LLM.BaseUrl, config.LLM.Model, config.LLM.ApiKey))
            response.Append(chunk);

        var answer = response.ToString().Trim();
        return answer.Length == 0 ? "模型没有给出回答。" : answer;
    }

    /// <summary>Splits on blank lines first, so a chunk never straddles two unrelated sections.</summary>
    private static List<string> SplitIntoChunks(string text, int chunkSize)
    {
        var chunks = new List<string>();
        var buffer = new System.Text.StringBuilder();

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.Length == 0)
            {
                if (buffer.Length > 0) { chunks.Add(buffer.ToString().Trim()); buffer.Clear(); }
                continue;
            }

            if (buffer.Length + line.Length + 1 > chunkSize && buffer.Length > 0)
            {
                chunks.Add(buffer.ToString().Trim());
                buffer.Clear();
            }

            if (line.Length > chunkSize)
            {
                if (buffer.Length > 0) { chunks.Add(buffer.ToString().Trim()); buffer.Clear(); }

                for (var start = 0; start < line.Length; start += chunkSize)
                    chunks.Add(line.Substring(start, Math.Min(chunkSize, line.Length - start)).Trim());

                continue;
            }

            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        if (buffer.Length > 0) chunks.Add(buffer.ToString().Trim());
        return chunks.Where(chunk => chunk.Length > 0).ToList();
    }
}
