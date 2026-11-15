using MyAiAssistant.Services.Mcp;

namespace DashboardTests;

/// <summary>An MCP backend that answers without spawning a process.</summary>
internal sealed class FakeMcpStatus : IMcpStatus
{
    private bool _connected;

    public Task<McpActionResult> StartAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.FromResult(new McpActionResult(true, "已连接 FakeWindowsMcp，发布 2 个工具"));
    }

    public Task<McpActionResult> StopAsync()
    {
        _connected = false;
        return Task.FromResult(new McpActionResult(true, "已停止 MCP 服务器并撤下它的工具"));
    }

    public McpStatusView Read() => new(
        Enabled: true,
        Connected: _connected,
        ServerCommand: @"D:\tools\python.exe",
        ServerName: _connected ? "FakeWindowsMcp" : "",
        ServerVersion: _connected ? "0.1.0" : "",
        ToolCount: _connected ? 2 : 0,
        ToolNames: _connected ? new[] { "click", "list_windows" } : Array.Empty<string>(),
        Error: null);
}
