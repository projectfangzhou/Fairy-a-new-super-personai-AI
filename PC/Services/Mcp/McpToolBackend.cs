using System.Text.Json;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

namespace MyAiAssistant.Services.Mcp;

/// <summary>What one attempted MCP tool call did.</summary>
public sealed record McpCallOutcome(bool Performed, bool IsError, string Message)
{
    public static McpCallOutcome Refused(string reason) => new(false, false, reason);
}

/// <summary>
/// The controlled adapter the checklist asks for: MCP is an optional backend, and every call goes
/// through the same door as the built-in screen tools — session state (pause/stop), the user's
/// approval, the audit log — before it reaches the server.
///
/// There is no allowlist. A third-party MCP tool can do anything its author wrote, and this app has
/// no way to know what that is, so every call is a question for the user.
/// </summary>
public sealed class McpToolBackend
{
    private readonly McpClient _client;
    private readonly ScreenSession _session;
    private readonly Func<ApprovalGate> _gate;
    private readonly Func<ActionRequest, CancellationToken, Task<bool>>? _approver;

    public McpToolBackend(McpClient client, ScreenSession session, Func<ApprovalGate> gate,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _approver = approver;
    }

    public McpClient Client => _client;

    public async Task<McpCallOutcome> CallAsync(string toolName, string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var permit = _session.BeginAction();
        if (!permit.Allowed)
            return McpCallOutcome.Refused(permit.Reason ?? ScreenRefusal.Paused);

        var performed = false;
        try
        {
            var request = new ActionRequest(
                $"mcp.{toolName}",
                Classify(toolName),
                $"调用 MCP 工具 {toolName}",
                toolName);

            var approval = await _gate().AuthorizeAsync(request, cancellationToken, _approver).ConfigureAwait(false);
            if (!approval.IsAllowed)
                return McpCallOutcome.Refused($"未执行：{approval.Reason}");

            var result = await _client.CallToolAsync(toolName, argumentsJson, cancellationToken).ConfigureAwait(false);
            performed = true;
            return new McpCallOutcome(true, result.IsError, result.Text);
        }
        catch (McpProtocolException ex)
        {
            return new McpCallOutcome(false, true, ex.Message);
        }
        catch (McpTransportException ex)
        {
            return new McpCallOutcome(false, true, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return McpCallOutcome.Refused("已取消");
        }
        finally
        {
            _session.EndAction(performed);
        }
    }

    /// <summary>
    /// Labels the audit entry. A tool whose name looks like UI input is recorded as operating the
    /// desktop; anything else is recorded as acting on an external service. Both require approval —
    /// the label only decides how the audit reads.
    /// </summary>
    internal static RiskCategory Classify(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return RiskCategory.ControlExternalDevice;

        var lowered = toolName.ToLowerInvariant();
        string[] uiActions = ["click", "type", "key", "scroll", "drag", "move", "launch", "start", "open", "close", "app"];

        return uiActions.Any(word => lowered.Contains(word, StringComparison.Ordinal))
            ? RiskCategory.OperateScreenAndInput
            : RiskCategory.ControlExternalDevice;
    }
}

/// <summary>
/// Presents one MCP tool to the agent as an ordinary tool. The agent sees a normal function; behind
/// it sits <see cref="McpToolBackend"/>, so there is no path that reaches the server unapproved.
/// </summary>
public sealed class McpToolAdapter : ITool
{
    private readonly McpToolBackend _backend;
    private readonly McpToolInfo _info;

    public McpToolAdapter(McpToolBackend backend, McpToolInfo info)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public string Name => "mcp_" + Sanitize(_info.Name);

    public string Description => string.IsNullOrWhiteSpace(_info.Description)
        ? $"MCP 工具 {_info.Name}"
        : _info.Description;

    public List<ToolParameter> Parameters => ReadParameters(_info.InputSchemaJson);

    public async Task<string> ExecuteAsync(System.Text.Json.Nodes.JsonObject args)
    {
        var result = await _backend.CallAsync(_info.Name, args.ToJsonString()).ConfigureAwait(false);

        if (!result.Performed) return result.Message;
        return result.IsError ? $"工具返回错误：{result.Message}" : result.Message;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    /// <summary>
    /// Maps the top level of the server's JSON schema onto the tool model this app uses. Nested
    /// schemas are flattened to their type name — the agent can still fill them in, it just gets
    /// less guidance than the server's own description would give.
    /// </summary>
    internal static List<ToolParameter> ReadParameters(string schemaJson)
    {
        var parameters = new List<ToolParameter>();

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
                foreach (var item in requiredElement.EnumerateArray())
                    if (item.GetString() is { } name)
                        required.Add(name);

            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return parameters;

            foreach (var property in properties.EnumerateObject())
            {
                var type = property.Value.TryGetProperty("type", out var typeElement) && typeElement.GetString() is { } declared
                    ? declared
                    : "string";
                var description = property.Value.TryGetProperty("description", out var descriptionElement)
                    ? descriptionElement.GetString() ?? ""
                    : "";

                parameters.Add(new ToolParameter
                {
                    Name = property.Name,
                    Type = type,
                    Description = description,
                    Required = required.Contains(property.Name),
                });
            }
        }
        catch (JsonException)
        {
            // A schema we cannot read simply yields no parameters; the call still carries the args.
        }

        return parameters;
    }
}
