using System.Text.Json;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>The embedding model to call, and how to call it. Empty model means "not configured".</summary>
public sealed record EmbeddingSettings(string BaseUrl, string Model, string ApiKey)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// Where to send embedding requests. An explicit URL wins; otherwise it is derived from the chat
    /// endpoint, because providers that offer chat completions almost always expose <c>/embeddings</c>
    /// next to it (<c>…/v1/chat/completions</c> → <c>…/v1/embeddings</c>). Deriving instead of asking
    /// keeps the settings screen to one field the user actually has to know.
    /// </summary>
    public static EmbeddingSettings From(string? configuredUrl, string? configuredModel, string? configuredKey,
        string? llmBaseUrl, string? llmApiKey)
    {
        var url = string.IsNullOrWhiteSpace(configuredUrl) ? DeriveUrl(llmBaseUrl) : configuredUrl!.Trim();
        var key = string.IsNullOrWhiteSpace(configuredKey) ? (llmApiKey ?? "") : configuredKey!.Trim();

        return new EmbeddingSettings(url, (configuredModel ?? "").Trim(), key.Trim());
    }

    public static string DeriveUrl(string? llmBaseUrl)
    {
        var url = (llmBaseUrl ?? "").Trim();
        if (url.Length == 0) return "";

        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url[..^"/chat/completions".Length] + "/embeddings";

        // Anything else is left alone: guessing at a provider's URL shape is how requests go to the
        // wrong place and fail with a 404 nobody can explain.
        return url;
    }
}

/// <summary>The answer to one embedding request: one vector per input, in input order.</summary>
public sealed record EmbeddingBatch(bool Ok, IReadOnlyList<float[]> Vectors, string? Error)
{
    public static EmbeddingBatch Fail(string error) => new(false, Array.Empty<float[]>(), error);
}

/// <summary>
/// OpenAI-compatible <c>/embeddings</c> wire format: <c>{model, input:[...]}</c> in, <c>data:[{index, embedding}]</c>
/// out.
///
/// The index field is not decorative — some providers return the items out of order, and pairing a
/// vector with the wrong paragraph would make retrieval quietly wrong instead of loudly broken.
/// </summary>
public static class EmbeddingsWire
{
    public const int MaxInputsPerCall = 32;

    public static string BuildBody(EmbeddingSettings settings, IReadOnlyList<string> inputs)
    {
        var payload = new
        {
            model = settings.Model,
            input = inputs,
            encoding_format = "float",
        };

        return JsonSerializer.Serialize(payload);
    }

    public static (bool Ok, IReadOnlyList<float[]> Vectors, string? Error) ParseResponse(int statusCode, string? body, int expected)
    {
        var payload = body ?? "";

        if (statusCode < 200 || statusCode > 299)
            return (false, Array.Empty<float[]>(), $"向量接口返回 HTTP {statusCode}{Detail(payload)}");

        if (string.IsNullOrWhiteSpace(payload))
            return (false, Array.Empty<float[]>(), "向量接口返回了空响应");

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
                return (false, Array.Empty<float[]>(),
                    "向量接口报错：" + (string.IsNullOrWhiteSpace(message) ? "没有给出原因" : message));
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (false, Array.Empty<float[]>(), "向量接口的响应里没有 data");

            var vectors = new float[expected][];
            var seen = 0;

            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexElement) &&
                            indexElement.ValueKind == JsonValueKind.Number
                    ? indexElement.GetInt32()
                    : seen;

                if (!item.TryGetProperty("embedding", out var embedding) ||
                    embedding.ValueKind != JsonValueKind.Array)
                    continue;

                var vector = new float[embedding.GetArrayLength()];
                var position = 0;
                foreach (var value in embedding.EnumerateArray())
                    vector[position++] = (float)value.GetDouble();

                if (index < 0 || index >= expected) continue;

                vectors[index] = vector;
                seen++;
            }

            if (seen != expected)
                return (false, Array.Empty<float[]>(), $"向量接口只回了 {seen} 条，期望 {expected} 条");

            var dimension = vectors[0]?.Length ?? 0;
            if (dimension == 0 || vectors.Any(vector => vector is null || vector.Length != dimension))
                return (false, Array.Empty<float[]>(), "向量接口回的向量长度不一致");

            return (true, vectors!, null);
        }
        catch (JsonException)
        {
            return (false, Array.Empty<float[]>(), "向量接口返回的内容不是 JSON");
        }
    }

    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length > 160) flat = flat[..160] + "…";

        return "（" + flat + "）";
    }
}

/// <summary>Vector arithmetic used by retrieval.</summary>
public static class VectorMath
{
    /// <summary>Cosine similarity; 0 when the vectors cannot be compared.</summary>
    public static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length) return 0;

        double dot = 0, lengthA = 0, lengthB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            lengthA += a[i] * (double)a[i];
            lengthB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(lengthA) * Math.Sqrt(lengthB);
        return denominator <= 1e-12 ? 0 : dot / denominator;
    }
}
