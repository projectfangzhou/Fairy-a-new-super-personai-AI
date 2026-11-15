namespace FairyAI_Android.Services.Usage;

/// <summary>
/// Token counts for one exchange. <see cref="Exact"/> says whether the provider reported them or the
/// app estimated them — the ledger keeps the two apart so a number on screen is never a guess dressed
/// up as a measurement.
/// </summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, bool Exact)
{
    public int TotalTokens => PromptTokens + CompletionTokens;

    public static TokenUsage None { get; } = new(0, 0, true);
}

/// <summary>
/// Reads one SSE chunk of a streaming chat completion.
///
/// Two things live in these chunks: the text delta, and — when the request asked for it with
/// <c>stream_options.include_usage</c> — a final chunk that carries the token counts. That usage
/// chunk has an empty <c>choices</c> array, so a parser that only looks at <c>choices[0]</c> silently
/// loses the only exact number the provider ever gives.
/// </summary>
public static class StreamChunkParser
{
    private const string SsePrefix = "data:";
    private const string DoneMarker = "[DONE]";

    public static bool IsDone(string? line) =>
        line is not null &&
        line.TrimStart().StartsWith(SsePrefix, StringComparison.Ordinal) &&
        line.Trim().EndsWith(DoneMarker, StringComparison.Ordinal);

    /// <summary>
    /// Parses one SSE line. Any problem — a keep-alive, a malformed chunk, a text-only delta — comes
    /// back as nulls instead of throwing, because a broken line in the middle of a stream must not
    /// kill the answer that is already being shown.
    /// </summary>
    public static (string? Text, TokenUsage? Usage) Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null);

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(SsePrefix, StringComparison.Ordinal)) return (null, null);

        var payload = trimmed[SsePrefix.Length..].Trim();
        if (payload.Length == 0 || payload == DoneMarker) return (null, null);

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);

            string? text = null;
            TokenUsage? usage = null;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].ValueKind == System.Text.Json.JsonValueKind.Object &&
                choices[0].TryGetProperty("delta", out var delta) &&
                delta.ValueKind == System.Text.Json.JsonValueKind.Object &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                text = content.GetString();
                if (text is { Length: 0 }) text = null;
            }

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var prompt = ReadInt(usageElement, "prompt_tokens");
                var completion = ReadInt(usageElement, "completion_tokens");

                if (prompt > 0 || completion > 0)
                    usage = new TokenUsage(prompt, completion, Exact: true);
            }

            return (text, usage);
        }
    }

    private static int ReadInt(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;
}
