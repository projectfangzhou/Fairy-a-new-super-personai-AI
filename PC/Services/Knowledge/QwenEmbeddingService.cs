using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyAiAssistant.Services.Knowledge;

/// <summary>
/// Embeds text with Qwen3-Embedding-0.6B (int8 ONNX) on this machine.
///
/// The 0.6B model is the desktop choice: ~585 MB on disk, ~1 GB resident, 1024-dimension vectors. It
/// runs locally, so retrieval costs nothing per question and works offline.
///
/// Two behaviours come from the model card and are easy to get wrong:
/// <list type="bullet">
/// <item>pooling is the <em>last</em> token, not a mean — a mean still produces a unit vector, so the
/// mistake hides until retrieval quality is bad;</item>
/// <item>queries are supposed to carry an instruction prefix while documents do not; embedding both
/// the same way costs a few points of accuracy without ever looking broken.</item>
/// </list>
/// </summary>
public sealed class QwenEmbeddingService : IDisposable
{
    public const string DefaultInstruction =
        "Given a Chinese question, retrieve the passages that answer it";

    private readonly object _gate = new();
    private readonly string _modelPath;
    private readonly string _vocabularyPath;
    private readonly string _mergesPath;
    private readonly string _addedTokensPath;

    private InferenceSession? _session;
    private BpeTokenizer? _tokenizer;
    private string? _error;

    public QwenEmbeddingService(string? directory = null)
    {
        var dir = directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dependencies", "qwen3-embedding");
        _modelPath = Path.Combine(dir, "model_quantized.onnx");
        _vocabularyPath = Path.Combine(dir, "vocab.json");
        _mergesPath = Path.Combine(dir, "merges.txt");
        _addedTokensPath = Path.Combine(dir, "added_tokens.json");
        Directory = dir;
    }

    public string Directory { get; }

    /// <summary>Texts embedded since start-up, for diagnostics.</summary>
    public int EmbeddedTexts { get; private set; }

    public long LastEmbedMilliseconds { get; private set; }

    public bool IsAvailable
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _session is not null;
            }
            catch (Exception ex)
            {
                _error = $"加载向量模型失败：{ex.GetType().Name}";
                return false;
            }
        }
    }

    public string? UnavailableReason
    {
        get
        {
            if (!File.Exists(_modelPath)) return $"没有找到模型文件：{_modelPath}";
            if (!File.Exists(_vocabularyPath) || !File.Exists(_mergesPath)) return "模型的词表文件不完整";
            return IsAvailable ? null : _error ?? "向量模型没能加载";
        }
    }

    public Task<float[]> EmbedDocumentAsync(string text) => Task.FromResult(Embed(text, isQuery: false));

    public Task<float[]> EmbedQueryAsync(string text, string? instruction = null) =>
        Task.FromResult(Embed(text, isQuery: true, instruction));

    private float[] Embed(string? text, bool isQuery, string? instruction = null)
    {
        EnsureLoaded();
        if (_session is null || _tokenizer is null) return Array.Empty<float>();

        var content = (text ?? "").Trim();
        if (content.Length == 0) return Array.Empty<float>();

        var input = isQuery
            ? $"Instruct: {(string.IsNullOrWhiteSpace(instruction) ? DefaultInstruction : instruction)}\nQuery: {content}"
            : content;

        var encoded = _tokenizer.Encode(input, "<|endoftext|>", maxLength: 512);

        var ids = new DenseTensor<long>(new[] { 1, encoded.Length });
        var mask = new DenseTensor<long>(new[] { 1, encoded.Length });

        for (var i = 0; i < encoded.Length; i++)
        {
            ids[0, i] = encoded.InputIds[i];
            mask[0, i] = encoded.AttentionMask[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };

        if (_session.InputNames.Contains("position_ids"))
        {
            var positions = new DenseTensor<long>(new[] { 1, encoded.Length });
            for (var i = 0; i < encoded.Length; i++) positions[0, i] = i;
            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", positions));
        }

        // This export is the decoder-shaped graph: it declares past_key_values.* inputs even when they
        // are empty. A zero-length cache is the correct input for a single forward pass — omitting the
        // tensors makes Concat fail with "Missing Input: past_key_values.0.key".
        foreach (var name in _session.InputNames)
        {
            if (!name.StartsWith("past_key_values", StringComparison.Ordinal)) continue;
            if (inputs.Any(input => input.Name == name)) continue;

            var metadata = _session.InputMetadata[name];
            var cacheShape = metadata.Dimensions
                .Select((dimension, index) => index == 0 ? 1 : Math.Max(0, dimension))
                .ToArray();

            inputs.Add(metadata.ElementType == typeof(long)
                ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(cacheShape))
                : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(cacheShape)));
        }

        var stopwatch = Stopwatch.StartNew();
        using var results = _session.Run(inputs);
        stopwatch.Stop();

        var first = results.FirstOrDefault();
        if (first is null) return Array.Empty<float>();

        var tensor = first.AsTensor<float>();
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length != 3) return Array.Empty<float>();

        var tokenCount = dimensions[1];
        var dimension = dimensions[2];

        var flat = new float[tokenCount * dimension];
        for (var token = 0; token < tokenCount; token++)
        for (var i = 0; i < dimension; i++)
            flat[token * dimension + i] = tensor[0, token, i];

        EmbeddedTexts++;
        LastEmbedMilliseconds = stopwatch.ElapsedMilliseconds;

        return QwenPooling.LastToken(flat, encoded.AttentionMask, tokenCount, dimension);
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;

        lock (_gate)
        {
            if (_session is not null) return;
            if (!File.Exists(_modelPath)) { _error = "没有找到模型文件"; return; }
            if (!File.Exists(_vocabularyPath) || !File.Exists(_mergesPath)) { _error = "模型的词表文件不完整"; return; }

            _tokenizer = BpeTokenizer.Load(_vocabularyPath, _mergesPath, _addedTokensPath);

            var options = new SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session = new InferenceSession(_modelPath, options);
            _error = null;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
