namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Where passage vectors come from. Two implementations exist and one is deliberately absent:
/// a remote <c>/embeddings</c> endpoint, a local model on the phone, and — until a native inference
/// runtime is wired in — nothing. Reporting "no runtime" is the honest state; pretending otherwise
/// would make retrieval look available when it cannot run.
/// </summary>
public interface IEmbeddingBackend
{
    /// <summary>Shown in the settings screen.</summary>
    string Name { get; }

    /// <summary>Cache namespace: vectors from two different backends are not comparable.</summary>
    string ModelKey { get; }

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

/// <summary>Runs a local model file. The missing piece is a native runtime, not this interface.</summary>
public interface IEmbeddingRuntime
{
    bool IsReady { get; }

    string? Reason { get; }

    Task<EmbeddingBatch> EmbedAsync(string modelPath, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

/// <summary>
/// The state this build is actually in: no ONNX Runtime / llama.cpp binding is linked in, so a local
/// model file can be present and still not be usable. It says so instead of failing later.
/// </summary>
public sealed class MissingEmbeddingRuntime : IEmbeddingRuntime
{
    public const string Explanation =
        "这个构建没有接入手机端的推理运行时（ONNX Runtime 或 llama.cpp 之一），所以本地向量模型还不能跑。" +
        "模型文件可以下载/导入，但在接上运行时之前，向量检索仍然走远程接口或退回关键词。";

    public bool IsReady => false;

    public string? Reason => Explanation;

    public Task<EmbeddingBatch> EmbedAsync(string modelPath, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmbeddingBatch.Fail(Explanation));
}

/// <summary>Vectors from the configured HTTP endpoint. Costs money per call; needs a key.</summary>
public sealed class RemoteEmbeddingBackend : IEmbeddingBackend
{
    private readonly EmbeddingClient _client;
    private readonly Func<EmbeddingSettings> _settings;

    public RemoteEmbeddingBackend(EmbeddingClient client, Func<EmbeddingSettings> settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Name => "远程接口";

    public string ModelKey
    {
        get
        {
            var settings = _settings();
            return settings.IsUsable ? "remote:" + settings.Model : "remote:unconfigured";
        }
    }

    public bool IsAvailable => _settings().IsUsable;

    public string? UnavailableReason =>
        IsAvailable ? null : "没有配置远程向量模型（模型名 + 接口地址）";

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        if (!settings.IsUsable) return Task.FromResult(EmbeddingBatch.Fail(UnavailableReason!));

        return _client.EmbedAsync(settings, inputs, cancellationToken);
    }
}

/// <summary>Vectors from a model file on the phone. Needs both the file and a runtime.</summary>
public sealed class LocalEmbeddingBackend : IEmbeddingBackend
{
    private readonly LocalEmbeddingStore _store;
    private readonly IEmbeddingRuntime _runtime;
    private readonly LocalEmbeddingModel _model;

    public LocalEmbeddingBackend(LocalEmbeddingStore store, IEmbeddingRuntime runtime, LocalEmbeddingModel model)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public LocalEmbeddingModel Model => _model;

    public string Name => "本地模型 " + _model.DisplayName;

    public string ModelKey => "local:" + _model.Id;

    public LocalModelState State => _store.StateOf(_model);

    public bool IsAvailable => _runtime.IsReady && State == LocalModelState.Ready;

    public string? UnavailableReason
    {
        get
        {
            if (!_runtime.IsReady) return _runtime.Reason ?? "没有可用的推理运行时";
            if (State == LocalModelState.Partial) return "模型文件下载不完整，需要重新下载";
            if (State == LocalModelState.Missing) return "还没有下载或导入模型文件";
            return null;
        }
    }

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return Task.FromResult(EmbeddingBatch.Fail(UnavailableReason!));

        return _runtime.EmbedAsync(_store.PathOf(_model), inputs, cancellationToken);
    }
}

/// <summary>
/// Picks the backend for this round: the local model when it can actually run, otherwise the remote
/// endpoint, otherwise nothing (and retrieval falls back to keywords, always available).
/// </summary>
public static class EmbeddingBackendSelector
{
    public static IEmbeddingBackend? Select(IEmbeddingBackend? local, IEmbeddingBackend? remote)
    {
        if (local is not null && local.IsAvailable) return local;
        if (remote is not null && remote.IsAvailable) return remote;
        return null;
    }

    /// <summary>One line for the settings screen: what would be used right now, and why when nothing is.</summary>
    public static string Describe(IEmbeddingBackend? local, IEmbeddingBackend? remote)
    {
        var chosen = Select(local, remote);
        if (chosen is not null) return $"向量检索会用：{chosen.Name}（{chosen.ModelKey}）";

        var reasons = new List<string>();
        if (local?.UnavailableReason is { Length: > 0 } localReason) reasons.Add("本地：" + localReason);
        if (remote?.UnavailableReason is { Length: > 0 } remoteReason) reasons.Add("远程：" + remoteReason);

        return reasons.Count == 0
            ? "现在只用关键词检索（离线、免费）：说法一致才找得到。"
            : "现在只用关键词检索。向量检索不可用 —— " + string.Join("；", reasons);
    }
}
