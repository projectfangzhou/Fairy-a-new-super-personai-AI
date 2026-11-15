using MyAiAssistant.Services.Dashboard;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// Where the engine gets its model from. <see cref="Environment"/> is applied to the child
/// process only; the key is never placed on the command line, because another process can
/// read a command line but not another process's environment on Windows.
/// </summary>
public sealed record PiModelBinding(
    string? ProviderArgument,
    string? ModelArgument,
    IReadOnlyDictionary<string, string> Environment,
    string DisplayName);

/// <summary>
/// Maps the app's configured endpoint onto the engine's provider model.
///
/// A provider is only passed through by id when the configured base URL really points at
/// that provider's canonical host. Otherwise the engine would quietly talk to a different
/// endpoint than the one the user configured — and with the user's key.
/// </summary>
public static class PiModelBindingFactory
{
    private static readonly (string Match, string ProviderId, string EnvName, string Host)[] Known =
    [
        ("deepseek", "deepseek", "DEEPSEEK_API_KEY", "deepseek.com"),
        ("openai", "openai", "OPENAI_API_KEY", "openai.com"),
        ("gemini", "google", "GEMINI_API_KEY", "googleapis.com"),
        ("google", "google", "GEMINI_API_KEY", "googleapis.com"),
        ("groq", "groq", "GROQ_API_KEY", "groq.com"),
        ("mistral", "mistral", "MISTRAL_API_KEY", "mistral.ai"),
        ("xai", "xai", "XAI_API_KEY", "x.ai"),
        ("nvidia", "nvidia", "NVIDIA_API_KEY", "nvidia.com"),
        ("openrouter", "openrouter", "OPENROUTER_API_KEY", "openrouter.ai")
    ];

    public static PiModelBinding? Create(EndpointSettings llm, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(llm);
        if (string.IsNullOrWhiteSpace(llm.Model)) return null;

        var known = MatchProvider(llm.Provider);
        if (known is { } entry && HostMatches(llm.BaseUrl, entry.Host))
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(apiKey)) env[entry.EnvName] = apiKey;
            return new PiModelBinding(entry.ProviderId, llm.Model, env,
                $"{entry.ProviderId}/{llm.Model}");
        }

        // Everything else — including a known provider pointed at a proxy — is registered by
        // our extension as an explicit OpenAI-compatible endpoint.
        var baseUrl = NormalizeBaseUrl(llm.BaseUrl);
        if (baseUrl is null) return null;

        var custom = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FAIRY_CUSTOM_BASE_URL"] = baseUrl,
            ["FAIRY_CUSTOM_MODEL"] = llm.Model
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) custom["FAIRY_CUSTOM_KEY"] = apiKey;

        var display = string.IsNullOrWhiteSpace(llm.Provider) ? "自定义端点" : llm.Provider;
        return new PiModelBinding("fairy-endpoint", llm.Model, custom, $"{display}/{llm.Model}");
    }

    /// <summary>
    /// The app stores full request URLs (".../v1/chat/completions") in some defaults while an
    /// OpenAI-compatible client expects the API root (".../v1"). Trim the difference instead
    /// of letting every request 404.
    /// </summary>
    internal static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        var normalized = uri.AbsoluteUri.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/completions" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        return normalized;
    }

    private static (string Match, string ProviderId, string EnvName, string Host)? MatchProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var normalized = provider.Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        foreach (var entry in Known)
            if (normalized.Contains(entry.Match, StringComparison.Ordinal))
                return entry;
        return null;
    }

    private static bool HostMatches(string? baseUrl, string expectedHost)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) &&
               uri.Host.Contains(expectedHost, StringComparison.OrdinalIgnoreCase);
    }
}
