using System.Text;
using System.Text.Json;

namespace FairyAI_Android.Services.Vision;

/// <summary>
/// Turns an HTTP response into an answer or a reason. Every failure path says what happened — a
/// vision call that returns "" and hopes the caller notices is the bug this replaces.
/// </summary>
public static class VisionReply
{
    public static (bool Ok, string Text, string? Error) Parse(int statusCode, string? body)
    {
        var payload = body ?? "";

        if (statusCode < 200 || statusCode > 299)
            return (false, "", $"视觉接口返回 HTTP {statusCode}{Detail(payload)}");

        if (string.IsNullOrWhiteSpace(payload))
            return (false, "", "视觉接口返回了空响应");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return (false, "", "视觉接口返回的内容不是 JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (false, "", "视觉接口返回的 JSON 不是对象");

            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
                return (false, "", "视觉接口报错：" + (string.IsNullOrWhiteSpace(message) ? "没有给出原因" : message));
            }

            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return (false, "", "视觉接口的响应里没有 choices");

            var first = choices[0];
            if (!first.TryGetProperty("message", out var messageElement))
                return (false, "", "视觉接口的响应里没有 message");

            var content = ReadContent(messageElement);
            if (string.IsNullOrWhiteSpace(content))
                return (false, "", "视觉模型返回了空内容（可能是内容审核或图像无法识别）");

            return (true, content.Trim(), null);
        }
    }

    /// <summary>
    /// Reads the message content. A string is the common shape; some providers return an array of
    /// parts, which is joined instead of being treated as empty.
    /// </summary>
    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";

        if (content.ValueKind != JsonValueKind.Array) return "";

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
                builder.Append(text.GetString());
        }

        return builder.ToString();
    }

    /// <summary>A short, de-identified hint from the body — never the whole response, which may echo the prompt.</summary>
    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length > 160) flat = flat[..160] + "…";

        return "（" + flat + "）";
    }
}
