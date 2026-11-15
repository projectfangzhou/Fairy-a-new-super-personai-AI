using System.Text.Json;
using System.Text.Json.Nodes;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

namespace MyAiAssistant.Services.Mcp;

/// <summary>
/// Owns the optional MCP server: starts it with the configured command, performs the handshake,
/// publishes its tools into the tool registry, and takes them out again on stop.
///
/// Nothing here weakens the gate: the published tools are <see cref="McpToolAdapter"/> instances,
/// and every call they make goes through <see cref="McpToolBackend"/> — session state, approval,
/// audit — exactly like the built-in screen tools.
/// </summary>
public sealed class McpControl : IMcpStatus, IAsyncDisposable
{
    private readonly ToolRegistry _registry;
    private readonly ScreenSession _session;
    private readonly Func<ApprovalGate> _gate;
    private readonly Func<McpSettings> _settings;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private McpClient? _client;
    private IReadOnlyList<McpToolInfo> _tools = Array.Empty<McpToolInfo>();
    private string? _error;
    private bool _enabled;

    public McpControl(ToolRegistry registry, ScreenSession session, Func<ApprovalGate> gate, Func<McpSettings> settings)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _enabled = settings().Enabled;
    }

    public McpStatusView Read() => new(
        _enabled,
        _client?.IsInitialized == true,
        _settings().ServerCommand,
        _client?.ServerInfo?.Name ?? "",
        _client?.ServerInfo?.Version ?? "",
        _tools.Count,
        _tools.Select(tool => tool.Name).ToArray(),
        _error);

    public async Task<McpActionResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            _enabled = settings.Enabled;

            if (!settings.Enabled)
                return new McpActionResult(false, "MCP 后端未启用；在配置里打开 Enabled 并填好服务器命令");
            if (!settings.HasServer)
                return new McpActionResult(false, "没有配置 MCP 服务器命令");
            if (_client?.IsInitialized == true)
                return new McpActionResult(true, $"MCP 服务器已在运行，{_tools.Count} 个工具");

            IMcpTransport transport;
            try
            {
                transport = StdioMcpTransport.Start(settings);
            }
            catch (McpTransportException ex)
            {
                _error = ex.Message;
                return new McpActionResult(false, ex.Message);
            }

            var client = new McpClient(transport)
            {
                RequestTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.StartupSeconds, 5, 300)),
            };
            try
            {
                await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);

                PublishTools(client, tools);

                _client = client;
                _tools = tools;
                _error = null;

                var name = client.ServerInfo?.Name ?? "MCP 服务器";
                return new McpActionResult(true, $"已连接 {name}，发布 {tools.Count} 个工具（每次调用都要你批准）");
            }
            catch (Exception ex) when (ex is McpProtocolException or McpTransportException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _error = ex.Message;
                return new McpActionResult(false, ex.Message);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<McpActionResult> StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is null) return new McpActionResult(true, "MCP 服务器没有在运行");

            UnpublishTools();
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            _tools = Array.Empty<McpToolInfo>();
            _error = null;
            return new McpActionResult(true, "已停止 MCP 服务器并撤下它的工具");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void PublishTools(McpClient client, IReadOnlyList<McpToolInfo> tools)
    {
        var backend = new McpToolBackend(client, _session, _gate);
        foreach (var tool in tools)
        {
            var adapter = new McpToolAdapter(backend, tool);
            _registry.Register(adapter);
        }
    }

    private void UnpublishTools()
    {
        foreach (var tool in _tools)
            _registry.Unregister("mcp_" + Sanitize(tool.Name));
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    /// <summary>Reads the arguments field: a JSON array as-is, otherwise whitespace separated.</summary>
    public static IReadOnlyList<string> ParseArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var array = JsonNode.Parse(trimmed)?.AsArray();
                if (array is not null)
                    return array.Where(node => node is not null)
                        .Select(node => node!.GetValue<string>())
                        .ToArray();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Fall through to whitespace splitting rather than failing the whole start.
            }
        }

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
