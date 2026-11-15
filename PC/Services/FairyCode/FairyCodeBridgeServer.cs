using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.Http;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>A decision answer handed back to the engine extension.</summary>
public sealed record BridgeDecision(bool Allowed, string Reason)
{
    public static BridgeDecision Allow() => new(true, "allowed");
    public static BridgeDecision Block(string reason) => new(false, reason);
}

/// <summary>
/// The loopback endpoint the Fairy Code engine extension calls before every tool call.
///
/// The token in the header only proves "this caller may ask". It never grants permission:
/// every decision is made by <see cref="IToolOracle"/>, which routes risky actions to the
/// human through the approval gate. That distinction is what stops a local process — the
/// engine itself included — from approving its own high-risk action on localhost.
/// </summary>
public sealed class FairyCodeBridgeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IToolOracle _oracle;
    private readonly LocalHttpServer _server;

    public FairyCodeBridgeServer(IToolOracle oracle)
    {
        _oracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
        // Per-run secret, never written to disk. It is passed to the engine process through
        // its environment, not through argv, because argv is readable by other processes.
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _server = new LocalHttpServer(HandleAsync);
    }

    public string Token { get; }

    public bool IsRunning => _server.IsRunning;

    public string? Url => _server.Url;

    /// <summary>Why the last <see cref="Start"/> failed, for the UI to show instead of guessing.</summary>
    public string? LastError => _server.LastError;

    public bool Start() => _server.Start(0);

    public void Stop() => _server.Stop();

    internal async Task<HttpResponseData> HandleAsync(HttpRequestData request)
    {
        if (!IsAuthorized(request.Headers.GetValueOrDefault("X-Fairy-Bridge")))
            return Json(401, new { ok = false, message = "缺少或无效的桥接令牌" });

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return Json(405, new { ok = false, message = "只支持 POST" });

        // The engine extension sends a fixed JSON body with Content-Length. A chunked request
        // is refused explicitly rather than being parsed as an empty body.
        if (request.Chunked)
            return Json(411, new { ok = false, message = "需要 Content-Length，不接受分块传输" });

        return request.Path.TrimEnd('/') switch
        {
            "/decide" => await DecideAsync(request.Body).ConfigureAwait(false),
            "/health" => Json(200, new { ok = true }),
            _ => Json(404, new { ok = false, message = "未知接口" })
        };
    }

    private async Task<HttpResponseData> DecideAsync(string body)
    {
        string taskId;
        EngineToolCall call;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Json(400, new { ok = false, message = "请求体必须是对象" });

            taskId = ReadString(root, "taskId") ?? string.Empty;
            var toolName = ReadString(root, "toolName") ?? string.Empty;

            var input = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in inputElement.EnumerateObject())
                {
                    // Values are flattened to strings because they only feed the classifier;
                    // structured payloads are serialized so a nested argument cannot hide.
                    input[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText();
                }
            }

            call = new EngineToolCall(toolName, input);
        }
        catch (JsonException)
        {
            return Json(400, new { ok = false, message = "请求体不是合法 JSON" });
        }

        if (taskId.Length == 0 || call.ToolName.Length == 0)
            return Json(400, new { ok = false, message = "需要 taskId 与 toolName" });

        BridgeDecision decision;
        try
        {
            var (allowed, reason) = await _oracle.DecideAsync(taskId, call, CancellationToken.None).ConfigureAwait(false);
            decision = allowed ? BridgeDecision.Allow() : BridgeDecision.Block(reason);
        }
        catch (Exception ex)
        {
            // Fail closed: a bridge failure must never read as an approval.
            decision = BridgeDecision.Block($"审批桥接失败：{ex.GetType().Name}");
        }

        return Json(200, new { ok = true, allow = decision.Allowed, reason = decision.Reason });
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public bool IsAuthorized(string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var expected = Encoding.UTF8.GetBytes(Token);
        var actual = Encoding.UTF8.GetBytes(provided);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static HttpResponseData Json(int status, object payload) =>
        new(status, JsonSerializer.Serialize(payload, JsonOptions));
}
