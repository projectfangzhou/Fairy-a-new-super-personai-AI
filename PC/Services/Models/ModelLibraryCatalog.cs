namespace MyAiAssistant.Services.Models;

/// <summary>Where a model entry comes from.</summary>
public enum ModelSource
{
    /// <summary>Local Ollama daemon. Listed first: it is the only group that can be switched to.</summary>
    Ollama = 0,
    LocalFile,
    CloudProvider
}

/// <summary>
/// What clicking "使用" actually does. Only <see cref="Ollama"/> rewrites the app config today, so
/// everything else is listed read-only rather than offering a button that would do nothing.
/// </summary>
public enum ModelActivation
{
    None = 0,
    Ollama
}

/// <summary>One row in the model library.</summary>
public sealed record ModelLibraryEntry(
    string Id,
    string Name,
    ModelSource Source,
    string Detail,
    bool IsActive,
    ModelActivation Activation,
    string? BlockedReason)
{
    /// <summary>True only when a click would really change which model the assistant uses.</summary>
    public bool CanActivate => Activation != ModelActivation.None && BlockedReason is null;

    public string SourceLabel => Source switch
    {
        ModelSource.Ollama => "Ollama",
        ModelSource.LocalFile => "本机文件",
        _ => "云端"
    };
}

public sealed record ModelLibrarySnapshot(
    IReadOnlyList<ModelLibraryEntry> Entries,
    string Summary,
    string ActiveLabel);

/// <summary>
/// Everything the catalogue needs, gathered by the caller. Kept as an input record so the
/// catalogue stays a pure function: it can be reasoned about and tested without touching config,
/// the filesystem or a running Ollama.
/// </summary>
public sealed record ModelLibraryInputs(
    IReadOnlyList<LocalModel> LocalFiles,
    IReadOnlyList<string> OllamaModels,
    bool OllamaRunning,
    IReadOnlyList<string> ProviderNames,
    Func<string, (string BaseUrl, string Model)?> ProviderInfo,
    string ActiveProvider,
    string ActiveModel);

/// <summary>
/// Turns the three places a model can live — files on disk, an Ollama daemon, and cloud presets —
/// into one ordered list with a single active marker.
///
/// The hard rule is that a row never implies capability it does not have: a model that cannot be
/// loaded by the chat pipeline carries a <see cref="ModelLibraryEntry.BlockedReason"/> and no
/// button, and an Ollama group that could not be read says why it is empty instead of silently
/// showing nothing.
/// </summary>
public static class ModelLibraryCatalog
{
    public const string LocalFileReason = "本机模型文件，尚未接入对话管线";
    public const string OllamaStoppedReason = "Ollama 服务未运行";
    public const string OllamaEmptyReason = "尚未拉取任何模型，先用 ollama pull 下载一个";
    public const string OllamaMissingReason = "已配置但未拉取，请重新 pull 或改选下面的模型";
    public const string CloudReason = "云端厂商各自需要 API Key，请到设置里切换";
    public const string CustomProviderDetail = "自定义地址，需在设置里填写";

    public static ModelLibrarySnapshot Build(ModelLibraryInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var entries = new List<ModelLibraryEntry>();
        var provider = inputs.ActiveProvider?.Trim() ?? "";
        var model = inputs.ActiveModel?.Trim() ?? "";

        var ollamaCount = 0;
        foreach (var raw in inputs.OllamaModels)
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            ollamaCount++;
            entries.Add(new ModelLibraryEntry(
                "ollama:" + name,
                name,
                ModelSource.Ollama,
                "本机 Ollama · localhost:11434",
                IsOllama(provider) && string.Equals(model, name, StringComparison.OrdinalIgnoreCase),
                ModelActivation.Ollama,
                inputs.OllamaRunning ? null : OllamaStoppedReason));
        }

        if (!inputs.OllamaRunning)
        {
            // An empty group with no explanation reads as a bug, so say why it is empty.
            entries.Add(new ModelLibraryEntry(
                "ollama:stopped",
                "Ollama 未运行",
                ModelSource.Ollama,
                "启动 Ollama 后这里会列出已 pull 的模型",
                false,
                ModelActivation.None,
                OllamaStoppedReason));
        }
        else if (ollamaCount == 0)
        {
            // A live daemon with nothing pulled is the normal fresh-install state; leaving the
            // group empty would read the same way as "cannot read the daemon".
            entries.Add(new ModelLibraryEntry(
                "ollama:empty",
                "Ollama 已运行",
                ModelSource.Ollama,
                "还没有已拉取的模型；拉取后这里会列出并可直接切换",
                false,
                ModelActivation.None,
                OllamaEmptyReason));
        }
        else if (IsOllama(provider) && model.Length > 0 &&
                 inputs.OllamaModels.All(m => !string.Equals(m?.Trim(), model, StringComparison.OrdinalIgnoreCase)))
        {
            // The config still points at a model that is no longer pulled. No row may claim the
            // active marker for it, but silence would leave the user wondering why nothing is
            // marked — so name the mismatch on its own row.
            entries.Add(new ModelLibraryEntry(
                "ollama:missing",
                model,
                ModelSource.Ollama,
                "这是当前对话的配置，但 Ollama 里已经没有这个模型",
                false,
                ModelActivation.None,
                OllamaMissingReason));
        }

        foreach (var file in inputs.LocalFiles)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Name)) continue;

            entries.Add(new ModelLibraryEntry(
                "local:" + file.Name,
                file.Name,
                ModelSource.LocalFile,
                DescribeFile(file),
                false,
                ModelActivation.None,
                LocalFileReason));
        }

        var cloudCount = 0;
        foreach (var name in inputs.ProviderNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Ollama already has its own group above, listing the models that are really pulled.
            // Listing it here again would put two "current" markers on the very same thing.
            if (IsOllama(name)) continue;

            cloudCount++;
            var info = inputs.ProviderInfo(name);
            var baseUrl = info?.BaseUrl;
            var preset = info?.Model;
            var active = string.Equals(provider, name, StringComparison.OrdinalIgnoreCase);

            entries.Add(new ModelLibraryEntry(
                "cloud:" + name,
                name,
                ModelSource.CloudProvider,
                string.IsNullOrWhiteSpace(baseUrl) ? CustomProviderDetail : Join(baseUrl, preset),
                active,
                ModelActivation.None,
                active ? null : CloudReason));
        }

        var ordered = entries
            .OrderBy(e => e.Source)
            .ThenByDescending(e => e.IsActive)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var activeLabel = string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model)
            ? "未配置"
            : $"{provider} / {model}";

        var summary = $"本机文件 {inputs.LocalFiles.Count} 个 · " +
                      $"Ollama {ollamaCount} 个 · " +
                      $"云端 {cloudCount} 个";

        return new ModelLibrarySnapshot(ordered, summary, activeLabel);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static bool IsOllama(string provider) =>
        provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFile(LocalModel file)
    {
        var quant = string.IsNullOrWhiteSpace(file.Quantization) || file.Quantization == "Unknown"
            ? file.Format
            : file.Quantization;

        return Join(FormatSize(file.SizeBytes), quant);
    }

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
