using System.Net.Http;
using System.Text;
using System.Text.Json;
using FairyAI_Android.Services.Usage;

namespace FairyAI_Android.Services;

public interface ILlmService
{
    /// <summary>
    /// Streams the answer. <paramref name="onUsage"/> is called once at the end with the token counts:
    /// exact when the provider reported them, otherwise an estimate flagged as such.
    /// </summary>
    IAsyncEnumerable<string> StreamChatAsync(
        List<Models.ChatMessage> history,
        string userMessage,
        Action<TokenUsage>? onUsage = null);

    Task<string> ChatAsync(List<Models.ChatMessage> history, string userMessage);
}

public class LlmService : ILlmService
{
    private readonly HttpClient _http = new();
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    public async Task<string> ChatAsync(List<Models.ChatMessage> history, string userMessage)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamChatAsync(history, userMessage))
            sb.Append(chunk);
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        List<Models.ChatMessage> history,
        string userMessage,
        Action<TokenUsage>? onUsage = null)
    {
        var appConfig = ConfigManager.Load();
        var config = appConfig.LLM;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            yield return "Please configure LLM API Key in settings";
            yield break;
        }

        var messages = new List<object>();
        var promptText = new StringBuilder();

        // Inject personality system prompt
        var personality = appConfig.Personality;
        if (!string.IsNullOrWhiteSpace(personality.SystemPrompt))
        {
            messages.Add(new { role = "system", content = personality.SystemPrompt });
            promptText.Append(personality.SystemPrompt).Append('\n');
        }

        // Add conversation history
        foreach (var m in history.TakeLast(20))
        {
            messages.Add(new { role = m.Role, content = m.Content });
            promptText.Append(m.Content).Append('\n');
        }

        messages.Add(new { role = "user", content = userMessage });
        promptText.Append(userMessage);

        // `stream_options.include_usage` is the only way a streaming answer reports real token counts.
        // Not every compatible endpoint accepts the field, so the plain body is kept as a fallback.
        var jsonWithUsage = JsonSerializer.Serialize(new
        {
            model = config.Model,
            messages,
            stream = true,
            max_tokens = 2048,
            stream_options = new { include_usage = true },
        });

        var jsonPlain = JsonSerializer.Serialize(new
        {
            model = config.Model,
            messages,
            stream = true,
            max_tokens = 2048,
        });

        var response = await TrySendAsync(config, jsonWithUsage, jsonPlain).ConfigureAwait(false);

        if (response is null)
        {
            var fallback = ConfigManager.Load().FallbackLLM;
            if (!string.IsNullOrWhiteSpace(fallback.ApiKey))
                response = await TrySendAsync(fallback, jsonWithUsage, jsonPlain).ConfigureAwait(false);
        }

        if (response is null)
        {
            yield return "LLM request failed";
            yield break;
        }

        var answer = new StringBuilder();
        TokenUsage? reported = null;

        using (response)
        {
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) continue;
                if (StreamChunkParser.IsDone(line)) break;

                var (text, usage) = StreamChunkParser.Parse(line);
                if (usage is not null) reported = usage;

                if (string.IsNullOrEmpty(text)) continue;

                answer.Append(text);
                yield return text;
            }
        }

        // The counts are reported even when nothing was streamed: a failed or empty answer still cost
        // prompt tokens, and pretending otherwise makes the ledger lie.
        onUsage?.Invoke(reported ?? new TokenUsage(
            TokenLedger.EstimateTokens(promptText.ToString()),
            TokenLedger.EstimateTokens(answer.ToString()),
            Exact: false));
    }

    /// <summary>
    /// Sends the request, keeping the "ask for usage" field only if the endpoint accepts it. A 400/422
    /// that names the field is retried without it; anything else is a real failure.
    /// </summary>
    private async Task<HttpResponseMessage?> TrySendAsync(LLMConfig target, string jsonWithUsage, string jsonPlain)
    {
        foreach (var json in new[] { jsonWithUsage, jsonPlain })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrWhiteSpace(target.ApiKey))
                    request.Headers.Add("Authorization", $"Bearer {target.ApiKey}");

                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;

                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                response.Dispose();

                var aboutUsageField =
                    body.Contains("stream_options", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("include_usage", StringComparison.OrdinalIgnoreCase);

                Log($"LLM HTTP {status}: {Clip(body)}");

                // Only the "this field is not supported" case is worth a second try.
                if (status is 400 or 422 && aboutUsageField && json == jsonWithUsage) continue;

                return null;
            }
            catch (Exception ex)
            {
                Log($"LLM request error: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private static string Clip(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [LLM] {msg}\n"); } catch { }
    }
}
