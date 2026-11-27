using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyAiAssistant.Services.Messaging;

public enum WebChatMessageKind
{
    /// <summary>A chat turn: the server answers with deltas and then a done frame.</summary>
    Message = 0,

    /// <summary>Liveness check. Answers without touching the model.</summary>
    Ping,

    /// <summary>Forget this connection's history.</summary>
    Reset,

    /// <summary>Anything else — answered with an error frame, never with silence.</summary>
    Unknown,

    /// <summary>A well-formed type whose payload is unusable (empty text, too long, bad JSON).</summary>
    Invalid
}

public sealed record WebChatIncoming(WebChatMessageKind Kind, string Text = "", string? Error = null);

/// <summary>
/// The JSON dialect spoken over the web chat socket. Every frame is one JSON object, so a client
/// never has to parse a framing convention on top of WebSocket's.
///
/// Both directions are pure string functions, which is what makes the protocol testable without a
/// socket, a model, or a running server.
/// </summary>
public static class WebChatProtocol
{
    public const int Version = 1;

    /// <summary>Longest prompt accepted. Longer than any sane typed message, short of a paste storm.</summary>
    public const int MaxPromptChars = 4_000;

    public static WebChatIncoming Parse(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
            return new(WebChatMessageKind.Invalid, Error: "空消息。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return new(WebChatMessageKind.Invalid, Error: "消息不是合法 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(WebChatMessageKind.Invalid, Error: "消息必须是 JSON 对象。");

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return new(WebChatMessageKind.Invalid, Error: "缺少 type 字段。");

            switch (typeElement.GetString())
            {
                case "ping":
                    return new(WebChatMessageKind.Ping);

                case "reset":
                    return new(WebChatMessageKind.Reset);

                case "message":
                    var text = root.TryGetProperty("text", out var textElement) &&
                               textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(text))
                        return new(WebChatMessageKind.Invalid, Error: "text 不能为空。");

                    if (text.Length > MaxPromptChars)
                        return new(WebChatMessageKind.Invalid, Error: $"text 超过 {MaxPromptChars} 字符上限。");

                    return new(WebChatMessageKind.Message, text.Trim());

                default:
                    return new(WebChatMessageKind.Unknown);
            }
        }
    }

    public static string Welcome(int maxPromptChars = MaxPromptChars) =>
        Json(new { type = "welcome", protocol = Version, maxPromptChars });

    public static string Delta(string text) => Json(new { type = "delta", text });

    public static string Done(int chars) => Json(new { type = "done", chars });

    public static string Pong() => Json(new { type = "pong" });

    public static string ResetAck() => Json(new { type = "reset" });

    public static string Error(string message) => Json(new { type = "error", message });

    private static string Json<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    private static readonly JsonSerializerOptions Options = new()
    {
        // Same choice as the dashboard API: leave CJK readable on the wire instead of turning every
        // character into a \uXXXX escape. The output is parsed as JSON, never injected into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
