namespace FairyAI_Android.Services.Vision;

/// <summary>
/// What a capability probe found out.
///
/// Three outcomes, not two: "it can see", "it answered but cannot see", and "the probe could not
/// tell". Collapsing the third into the second is how a single network hiccup permanently disables a
/// feature — the flag would say "this model cannot see images" when the truth is "nobody asked it".
/// </summary>
public sealed record VisionProbeResult(bool CanSeeImages, bool Conclusive, string Message)
{
    public static VisionProbeResult Sees(string message) => new(true, true, message);

    public static VisionProbeResult Blind(string message) => new(false, true, message);

    /// <summary>No answer either way: wrong key, server error, timeout, no network.</summary>
    public static VisionProbeResult Unknown(string message) => new(false, false, message);
}

/// <summary>
/// Asks the endpoint one question that can only be answered by actually seeing an image.
///
/// There is no way to tell "is this model omnimodal" from a model name: providers ship both vision
/// and text models under the same family, and the same name means different things across vendors.
/// So the app does not guess — it sends a 16×16 solid-colour square and reads the answer.
/// </summary>
public static class VisionProbe
{
    /// <summary>A 16×16 pure red square. Small enough that probing costs almost nothing.</summary>
    private const string RedSquarePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAGklEQVR42mO8IyfHQApgYiARjGoY1TB0NAAAjEsBOPY5fDQAAAAASUVORK5CYII=";

    public const string Prompt = "这张图是一个纯色方块。它是什么颜色？只回答颜色，不要解释。";

    /// <summary>The expected answer, in the languages the app speaks.</summary>
    private static readonly string[] ColorWords = { "红", "red", "dc1e1e" };

    private static readonly byte[] BaseImage = Convert.FromBase64String(RedSquarePngBase64);

    public static byte[] SampleImage() => (byte[])BaseImage.Clone();

    /// <summary>Interprets the reply to the probe.</summary>
    public static VisionProbeResult Classify(int statusCode, string? body)
    {
        var (ok, text, error) = VisionReply.Parse(statusCode, body);

        if (!ok)
        {
            var reason = error ?? "没有回应";

            // The endpoint answered clearly: it does not take images.
            if (statusCode is 400 or 415 or 422 ||
                reason.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("multimodal", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("vision", StringComparison.OrdinalIgnoreCase))
                return VisionProbeResult.Blind($"这个接口不接受图片：{reason}");

            // Everything else is about the request, not about the model's eyesight.
            if (statusCode is 401 or 403)
                return VisionProbeResult.Unknown($"鉴权没通过（HTTP {statusCode}）：先把 API Key 填对再测");

            if (statusCode >= 500)
                return VisionProbeResult.Unknown($"接口自己出错了（HTTP {statusCode}），没测出结果");

            return VisionProbeResult.Unknown(reason);
        }

        var lower = text.ToLowerInvariant();
        if (ColorWords.Any(word => lower.Contains(word, StringComparison.Ordinal)))
            return VisionProbeResult.Sees("能用：它认出了这张图是红色");

        return VisionProbeResult.Blind(
            $"接口有回应，但没认出图里的红色（它答的是「{Clip(text)}」），可能没有真的在看图");
    }

    private static string Clip(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 40 ? flat : flat[..40] + "…";
    }
}
