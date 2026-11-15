namespace MyAiAssistant.Services.Mcp;

/// <summary>One tool the MCP server advertises.</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

public sealed record McpServerInfo(string Name, string Version, string ProtocolVersion);

/// <summary>The result of one tool call, already reduced to text.</summary>
public sealed record McpCallResult(bool IsError, string Text);

/// <summary>
/// Where the MCP server lives. The command is a fixed path the user configured — it is started
/// directly, never through a shell, and never resolved from a modified PATH.
/// </summary>
public sealed record McpSettings(
    bool Enabled,
    string ServerCommand,
    IReadOnlyList<string> ServerArguments,
    int StartupSeconds = 30)
{
    public static McpSettings Disabled { get; } = new(false, "", Array.Empty<string>());

    public bool HasServer => !string.IsNullOrWhiteSpace(ServerCommand);
}

/// <summary>The server answered, but not with something the protocol allows.</summary>
public sealed class McpProtocolException : Exception
{
    public McpProtocolException(string message, int? code = null) : base(message) => Code = code;

    public int? Code { get; }
}

/// <summary>The server process is not running or went away.</summary>
public sealed class McpTransportException : Exception
{
    public McpTransportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What the dashboard may do with the optional MCP backend.</summary>
public interface IMcpStatus
{
    McpStatusView Read();

    Task<McpActionResult> StartAsync(CancellationToken cancellationToken = default);

    Task<McpActionResult> StopAsync();
}

/// <summary>What the control centre shows about the MCP backend.</summary>
public sealed record McpStatusView(
    bool Enabled,
    bool Connected,
    string ServerCommand,
    string ServerName,
    string ServerVersion,
    int ToolCount,
    IReadOnlyList<string> ToolNames,
    string? Error);

public sealed record McpActionResult(bool Ok, string Message);
