using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Runs an ONNX embedding model on the phone.
///
/// This is the piece that makes "自带一个向量模型" real: the model file ships in the APK (or is
/// imported), and everything happens on the device — no key, no network, no per-call cost.
///
/// Deliberate choices: the session is created once and reused (loading a model costs seconds), the
/// tokenizer is loaded from the vocabulary file next to it, and every failure comes back as text
/// rather than an exception, because a retrieval that cannot embed must degrade to keyword search
/// instead of breaking the question.
/// </summary>
public sealed class OnnxEmbeddingRuntime : IEmbeddingRuntime, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string> _modelPath;
    private readonly Func<string> _vocabPath;

    private InferenceSession? _session;
    private WordPieceTokenizer? _tokenizer;
    private string? _loadedModel;
    private string? _loadError;

    public OnnxEmbeddingRuntime(Func<string> modelPath, Func<string> vocabPath)
    {
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _vocabPath = vocabPath ?? throw new ArgumentNullException(nameof(vocabPath));
    }

    /// <summary>How many texts were embedded since start-up, for the settings screen.</summary>
    public int EmbeddedTexts { get; private set; }

    public bool IsReady
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
                _loadError = $"加载向量模型失败：{ex.GetType().Name}";
                return false;
            }
        }
    }

    public string? Reason
    {
        get
        {
            var path = _modelPath();
            if (!File.Exists(path)) return "还没有模型文件";
            if (!File.Exists(_vocabPath())) return "模型的词表文件不在";

            return IsReady ? null : _loadError ?? "向量模型没能加载";
        }
    }

    public async Task<EmbeddingBatch> EmbedAsync(
        string modelPath,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLoaded();
            if (_session is null || _tokenizer is null)
                return EmbeddingBatch.Fail(_loadError ?? Reason ?? "向量模型不可用");

            var texts = (inputs ?? Array.Empty<string>()).ToList();
            if (texts.Count == 0) return new EmbeddingBatch(true, Array.Empty<float[]>(), null);

            var vectors = new List<float[]>(texts.Count);

            // One text at a time: the batch bookkeeping of the ONNX input is the fiddly part, and a
            // document-sized batch would multiply peak memory on a phone for no real gain.
            foreach (var text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vector = EmbedOne(text);
                if (vector.Length == 0) return EmbeddingBatch.Fail("向量模型返回了空的向量");

                vectors.Add(vector);
            }

            return new EmbeddingBatch(true, vectors, null);
        }
        catch (OperationCanceledException)
        {
            return EmbeddingBatch.Fail("向量计算被取消了");
        }
        catch (Exception ex)
        {
            return EmbeddingBatch.Fail($"本机向量计算出错：{ex.GetType().Name}");
        }
    }

    private float[] EmbedOne(string text)
    {
        var session = _session!;
        var tokenized = _tokenizer!.Tokenize(text);

        var ids = new DenseTensor<long>(new[] { 1, tokenized.Length });
        var mask = new DenseTensor<long>(new[] { 1, tokenized.Length });
        var types = new DenseTensor<long>(new[] { 1, tokenized.Length });

        for (var i = 0; i < tokenized.Length; i++)
        {
            ids[0, i] = tokenized.InputIds[i];
            mask[0, i] = tokenized.AttentionMask[i];
            types[0, i] = tokenized.TokenTypeIds[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };

        // BERT-family exports differ on whether token_type_ids is an input; only send what exists.
        if (session.InputNames.Contains("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        using var results = session.Run(inputs);

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
        return TokenPooling.MeanPool(flat, tokenized.AttentionMask, tokenCount, dimension);
    }

    private void EnsureLoaded()
    {
        var path = _modelPath();
        if (_session is not null && _loadedModel == path) return;

        lock (_gate)
        {
            if (_session is not null && _loadedModel == path) return;

            var vocabPath = _vocabPath();
            if (!File.Exists(path)) { _loadError = "还没有模型文件"; return; }
            if (!File.Exists(vocabPath)) { _loadError = "模型的词表文件不在"; return; }

            _tokenizer = WordPieceTokenizer.FromVocabulary(File.ReadAllLines(vocabPath));

            var options = new SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = 2,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session?.Dispose();
            _session = new InferenceSession(path, options);
            _loadedModel = path;
            _loadError = null;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
