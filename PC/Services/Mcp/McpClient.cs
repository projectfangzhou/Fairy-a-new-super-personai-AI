using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAiAssistant.Services.Mcp;

/// <summary>
/// A real MCP client: JSON-RPC 2.0 over the transport, with the handshake
/// (<c>initialize</c> → <c>notifications/initialized</c>), <c>tools/list</c> and <c>tools/call</c>.
///
/// The previous <c>MCPClient</c> in this repository invented three HTTP endpoints (<c>/tools</c>,
/// <c>/call</c>, <c>/connect</c>) that no MCP server serves; this one speaks the protocol.
///
/// One request is in flight at a time (the stdio transport is a single stream), and responses are
/// matched by id, so a server that emits log notifications between requests cannot desynchronise us.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const string DefaultProtocolVersion = "2025-06-18";
    private const int MaxSkippedMessages = 200;

    private readonly IMcpTransport _transport;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly string _clientName;
    private readonly string _clientVersion;
    private int _nextId;

    public McpClient(IMcpTransport transport, string clientName = "Xianling", string clientVersion = "3.0")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clientName = clientName;
        _clientVersion = clientVersion;
    }

    public McpServerInfo? ServerInfo { get; private set; }

    public bool IsInitialized { get; private set; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<McpServerInfo> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = DefaultProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = _clientName,
                ["version"] = _clientVersion,
            },
        };

        var result = await SendRequestAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);

        var version = result.TryGetProperty("protocolVersion", out var versionElement)
            ? versionElement.GetString() ?? DefaultProtocolVersion
            : DefaultProtocolVersion;

        var serverInfo = result.TryGetProperty("serverInfo", out var infoElement)
            ? new McpServerInfo(
                infoElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "",
                infoElement.TryGetProperty("version", out var serverVersionElement) ? serverVersionElement.GetString() ?? "" : "",
                version)
            : new McpServerInfo("", "", version);

        // The handshake is only complete once the client says so; servers are allowed to wait for it.
        await _transport.SendLineAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(),
            cancellationToken).ConfigureAwait(false);

        ServerInfo = serverInfo;
        IsInitialized = true;
        return serverInfo;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;

        do
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            var result = await SendRequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);

            if (result.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in toolsElement.EnumerateArray())
                {
                    var name = tool.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var description = tool.TryGetProperty("description", out var descriptionElement)
                        ? descriptionElement.GetString() ?? ""
                        : "";
                    var schema = tool.TryGetProperty("inputSchema", out var schemaElement)
                        ? schemaElement.GetRawText()
                        : "{\"type\":\"object\",\"properties\":{}}";

                    tools.Add(new McpToolInfo(name!, description, schema));
                }
            }

            cursor = result.TryGetProperty("nextCursor", out var cursorElement) ? cursorElement.GetString() : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        JsonNode arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(argumentsJson) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new McpProtocolException($"工具参数不是合法 JSON：{ex.Message}");
        }

        var parameters = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments,
        };

        var result = await SendRequestAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);

        var isError = result.TryGetProperty("isError", out var isErrorElement) &&
                      isErrorElement.ValueKind is JsonValueKind.True;

        return new McpCallResult(isError, ReadContent(result));
    }

    private static string ReadContent(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "text" && item.TryGetProperty("text", out var textElement))
                parts.Add(textElement.GetString() ?? "");
            else
                parts.Add($"[{type ?? "unknown"} content]");
        }

        return string.Join("\n", parts);
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters is not null) request["params"] = parameters;

            await _transport.SendLineAsync(request.ToJsonString(), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            for (var skipped = 0; skipped < MaxSkippedMessages; skipped++)
            {
                var line = await _transport.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                    throw new McpTransportException(_transport.LastError ?? "MCP 服务器关闭了输出流");
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A server printing a stray log line to stdout must not break the session.
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;

                    if (root.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.Number &&
                        idElement.TryGetInt64(out var responseId) && responseId == id)
                    {
                        if (root.TryGetProperty("error", out var errorElement))
                        {
                            var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
                                ? parsed
                                : (int?)null;
                            var message = errorElement.TryGetProperty("message", out var messageElement)
                                ? messageElement.GetString() ?? "未说明原因"
                                : "未说明原因";
                            throw new McpProtocolException($"{method} 失败：{message}", code);
                        }

                        if (root.TryGetProperty("result", out var resultElement))
                            return resultElement.Clone();

                        throw new McpProtocolException($"{method} 的响应里既没有 result 也没有 error");
                    }

                    // A server-to-client request: answer it so the server does not wait forever.
                    if (root.TryGetProperty("method", out _) &&
                        root.TryGetProperty("id", out var incomingId) &&
                        incomingId.ValueKind == JsonValueKind.Number)
                    {
                        var answer = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = incomingId.GetInt64(),
                            ["error"] = new JsonObject
                            {
                                ["code"] = -32601,
                                ["message"] = "客户端不支持该请求",
                            },
                        };
                        await _transport.SendLineAsync(answer.ToJsonString(), cancellationToken).ConfigureAwait(false);
                    }

                    // Anything else is a notification or noise; keep reading.
                }
            }

            throw new McpProtocolException($"{method} 的响应没有在 {MaxSkippedMessages} 条消息内出现");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpTransportException($"等待 MCP 服务器响应超时（{RequestTimeout.TotalSeconds:F0} 秒）");
        }
        finally
        {
            _io.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _io.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
