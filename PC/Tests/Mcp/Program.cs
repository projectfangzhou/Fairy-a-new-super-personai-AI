using System.Text.Json;
using System.Text.Json.Nodes;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Mcp;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-mcp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ---------------------------------------------------------------- handshake

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);

        var info = await client.InitializeAsync();
        Assert(info.Name == "FakeWindowsMcp" && info.Version == "0.1.0", "serverInfo is parsed");
        Assert(client.IsInitialized, "the client records that it is initialized");

        var initialize = transport.Requests("initialize").Single();
        Assert(initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString() == "Xianling",
            "initialize carries the client name");
        Assert(initialize.GetProperty("params").GetProperty("protocolVersion").GetString() is { Length: > 0 },
            "initialize declares a protocol version");
        Assert(transport.Methods.SequenceEqual(new[] { "initialize", "notifications/initialized" }),
            "the initialized notification follows the handshake");
        Assert(transport.Sent[1].Contains("\"method\":\"notifications/initialized\"", StringComparison.Ordinal) &&
               !transport.Sent[1].Contains("\"id\"", StringComparison.Ordinal),
            "the notification carries no id");
    }

    // ---------------------------------------------------------------- tools/list

    {
        var transport = new FakeMcpServer { Pages = 2, EmitNoise = true };
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var tools = await client.ListToolsAsync();
        Assert(tools.Count == 3, "both pages are read");
        Assert(tools[0].Name == "click" && tools[2].Name == "list_windows", "tool names survive pagination");
        Assert(tools[0].InputSchemaJson.Contains("x", StringComparison.Ordinal), "the schema is kept");

        var lists = transport.Requests("tools/list");
        Assert(lists.Count == 2, "a second page is requested");
        Assert(lists[1].GetProperty("params").GetProperty("cursor").GetString() == "page-2", "the cursor is passed back");
    }

    // ---------------------------------------------------------------- tools/call

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var result = await client.CallToolAsync("click", "{\"x\":10,\"y\":20}");
        Assert(!result.IsError && result.Text == "clicked at 10,20", "text content is returned");

        var call = transport.Requests("tools/call").Single();
        Assert(call.GetProperty("params").GetProperty("name").GetString() == "click", "the tool name is sent");
        Assert(call.GetProperty("params").GetProperty("arguments").GetProperty("x").GetInt32() == 10,
            "the arguments object is passed through");

        transport.RespondWithIsError = true;
        var failed = await client.CallToolAsync("click", "{}");
        Assert(failed.IsError, "isError is surfaced");
    }

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var badArguments = false;
        try { await client.CallToolAsync("click", "{not json"); }
        catch (McpProtocolException) { badArguments = true; }
        Assert(badArguments, "malformed arguments are refused before sending");
    }

    // ---------------------------------------------------------------- errors and oddities

    {
        var transport = new FakeMcpServer { FailWithError = true };
        await using var client = new McpClient(transport);

        var error = false;
        try { await client.InitializeAsync(); }
        catch (McpProtocolException ex) { error = ex.Code == -32603 && ex.Message.Contains("-32603", StringComparison.Ordinal) == false; }
        Assert(error, "a JSON-RPC error becomes a protocol exception with its code");
    }

    {
        // A server that asks the client for something must get an answer, otherwise it may block.
        var transport = new FakeMcpServer { AskClientFirst = true };
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        Assert(transport.Sent.Any(line => line.Contains("-32601", StringComparison.Ordinal)),
            "an unsupported server-to-client request is answered with method-not-found");
    }

    {
        var transport = new FakeMcpServer { NeverAnswer = true };
        await using var client = new McpClient(transport) { RequestTimeout = TimeSpan.FromMilliseconds(150) };

        var timedOut = false;
        try { await client.InitializeAsync(); }
        catch (McpTransportException ex) { timedOut = ex.Message.Contains("超时", StringComparison.Ordinal); }
        Assert(timedOut, "a silent server is reported as a timeout, not a hang");
    }

    // ---------------------------------------------------------------- the gate in front of MCP

    ScreenSession Session() => new();

    McpToolBackend Backend(FakeMcpServer server, McpClient client, ScreenSession session, ApprovalGate gate,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver) =>
        new(client, session, () => gate, approver);

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var session = Session();
        var audit = new OperationAudit(Path.Combine(directory, "approved"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var asked = 0;
        var backend = Backend(transport, client, session, gate,
            (_, _) => { asked++; return Task.FromResult(true); });

        var outcome = await backend.CallAsync("click", "{\"x\":1,\"y\":2}");
        Assert(outcome.Performed && outcome.Message.Contains("clicked", StringComparison.Ordinal),
            "an approved MCP call runs");
        Assert(asked == 1, "the user was asked once");
        Assert(transport.Requests("tools/call").Count == 1, "exactly one tools/call was sent");
        Assert(session.CompletedActions == 1 && session.InFlightActions == 0, "the session is released afterwards");

        var records = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert(records.Any(r => r.Category == RiskCategory.OperateScreenAndInput),
            "a UI-looking tool is audited as operating the desktop");
    }

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var session = Session();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "state")));
        var backend = Backend(transport, client, session, gate, (_, _) => Task.FromResult(true));

        await backend.CallAsync("get_active_window", "{}");
        var records = gate.RecordsFor(Path.Combine(directory, "state"));
        Assert(records.Any(r => r.Category == RiskCategory.ControlExternalDevice),
            "a query-looking tool is audited as an external service call");
    }

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var session = Session();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "denied")));
        var backend = Backend(transport, client, session, gate, (_, _) => Task.FromResult(false));

        var denied = await backend.CallAsync("click", "{}");
        Assert(!denied.Performed && denied.Message.Contains("未执行", StringComparison.Ordinal),
            "a refused call never reaches the server");
        Assert(transport.Requests("tools/call").Count == 0, "and nothing was sent");

        session.Stop();
        var afterStop = await backend.CallAsync("click", "{}");
        Assert(!afterStop.Performed && afterStop.Message.Contains("停止", StringComparison.Ordinal),
            "a stopped session refuses MCP calls too");

        var noApprover = Backend(transport, client, Session(), gate, null);
        var failClosed = await noApprover.CallAsync("click", "{}");
        Assert(!failClosed.Performed && transport.Requests("tools/call").Count == 0,
            "no approver means no call");
    }

    // ---------------------------------------------------------------- the adapter the agent sees

    {
        var transport = new FakeMcpServer();
        await using var client = new McpClient(transport);
        await client.InitializeAsync();

        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "adapter")));
        var backend = new McpToolBackend(client, Session(), () => gate, (_, _) => Task.FromResult(true));

        var info = new McpToolInfo("click", "Click at coordinates",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\",\"description\":\"X\"},\"label\":{\"type\":\"string\"}},\"required\":[\"x\"]}");
        var adapter = new McpToolAdapter(backend, info);

        Assert(adapter.Name == "mcp_click", "the tool name is namespaced");
        Assert(adapter.Description == "Click at coordinates", "the description is kept");
        Assert(adapter.Parameters.Count == 2, "both parameters are exposed");
        Assert(adapter.Parameters[0].Type == "number" && adapter.Parameters[0].Required, "types and required flags map");
        Assert(!adapter.Parameters[1].Required, "an optional parameter stays optional");

        var result = await adapter.ExecuteAsync(new JsonObject { ["x"] = 3, ["label"] = "ok" });
        Assert(result.Contains("clicked", StringComparison.Ordinal), "the adapter returns the tool's text");

        transport.RespondWithIsError = true;
        var failed = await adapter.ExecuteAsync(new JsonObject());
        Assert(failed.StartsWith("工具返回错误", StringComparison.Ordinal), "an error result is labelled as an error");

        var spaced = new McpToolAdapter(backend, new McpToolInfo("take-screenshot now", "", "{}"));
        Assert(spaced.Name == "mcp_take_screenshot_now", "unsafe characters in a tool name are replaced");
        Assert(spaced.Parameters.Count == 0, "an unreadable schema yields no parameters instead of throwing");
    }

    // ---------------------------------------------------------------- settings parsing

    {
        Assert(McpControl.ParseArguments("[\"-m\",\"windows_mcp\"]").SequenceEqual(new[] { "-m", "windows_mcp" }),
            "a JSON array of arguments is honoured");
        Assert(McpControl.ParseArguments(" -m  windows_mcp ").SequenceEqual(new[] { "-m", "windows_mcp" }),
            "whitespace separated arguments work too");
        Assert(McpControl.ParseArguments("").Count == 0, "empty arguments are empty");
        Assert(McpControl.ParseArguments("[broken").SequenceEqual(new[] { "[broken" }),
            "a broken array falls back to splitting rather than failing the start");
    }

    Console.WriteLine($"All {checks} MCP checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}

internal static class AuditExtensions
{
    public static IReadOnlyList<OperationRecord> RecordsFor(this ApprovalGate gate, string directory) =>
        new OperationAudit(directory).Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
}

/// <summary>
/// A stand-in MCP server: it answers the three methods this client uses, on the same stream, with
/// scripted quirks (pagination, noise, errors, silence).
/// </summary>
internal sealed class FakeMcpServer : IMcpTransport
{
    private readonly Queue<string?> _incoming = new();
    private int _toolListCalls;

    public List<string> Sent { get; } = new();

    public List<string> Methods { get; } = new();

    public int Pages { get; init; } = 1;

    public bool EmitNoise { get; init; }

    public bool FailWithError { get; init; }

    public bool AskClientFirst { get; init; }

    public bool NeverAnswer { get; init; }

    public bool RespondWithIsError { get; set; }

    public bool IsConnected => true;

    public string? LastError => null;

    public IReadOnlyList<JsonElement> Requests(string method) =>
        Sent.Select(TryParse)
            .Where(element => element is not null &&
                              element.Value.TryGetProperty("method", out var m) && m.GetString() == method)
            .Select(element => element!.Value.Clone())
            .ToArray();

    public Task SendLineAsync(string json, CancellationToken cancellationToken = default)
    {
        Sent.Add(json);
        if (NeverAnswer) return Task.CompletedTask;

        var element = TryParse(json);
        if (element is null) return Task.CompletedTask;

        var root = element.Value;
        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        if (method is not null) Methods.Add(method);

        if (method == "notifications/initialized") return Task.CompletedTask;

        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt64()
            : (long?)null;
        if (id is null) return Task.CompletedTask;

        if (AskClientFirst)
        {
            _incoming.Enqueue("{\"jsonrpc\":\"2.0\",\"id\":900,\"method\":\"sampling/createMessage\"}");
            AskClientFirstReset();
        }

        if (EmitNoise)
        {
            _incoming.Enqueue("这是服务器打到 stdout 的一行日志，不是 JSON");
            _incoming.Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{\"level\":\"info\"}}");
        }

        _incoming.Enqueue(Answer(method, id.Value));
        return Task.CompletedTask;
    }

    private bool _asked;
    private void AskClientFirstReset() => _asked = true;

    private string Answer(string? method, long id) => method switch
    {
        "initialize" => FailWithError
            ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32603,\"message\":\"内部错误\"}}"
            : "{\"jsonrpc\":\"2.0\",\"id\":" + id +
              ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"serverInfo\":{\"name\":\"FakeWindowsMcp\",\"version\":\"0.1.0\"},\"capabilities\":{\"tools\":{}}}}",
        "tools/list" => ToolsPage(id),
        "tools/call" => RespondWithIsError
            ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"boom\"}]}}"
            : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"clicked at 10,20\"}]}}",
        _ => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"unknown method\"}}",
    };

    private string ToolsPage(long id)
    {
        _toolListCalls++;
        var first = "{\"name\":\"click\",\"description\":\"Click\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}}}}";
        var third = "{\"name\":\"list_windows\",\"description\":\"List\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}";

        if (Pages <= 1)
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[" + first + "," + third + "]}}";

        return _toolListCalls == 1
            ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[" + first + "],\"nextCursor\":\"page-2\"}}"
            : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"type_text\",\"description\":\"Type\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," + third + "]}}";
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        // "Silent server": never produce a line, so only the client's own deadline can end the wait.
        if (NeverAnswer) await Task.Delay(Timeout.Infinite, cancellationToken);

        return _incoming.Count > 0 ? _incoming.Dequeue() : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static JsonElement? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
