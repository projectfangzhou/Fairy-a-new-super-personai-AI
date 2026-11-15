using System.Diagnostics;
using System.IO;
using System.Text;

namespace MyAiAssistant.Services.Mcp;

/// <summary>
/// One line in, one line out. MCP's stdio transport is newline-delimited JSON-RPC 2.0, and keeping
/// the framing behind this interface is what lets the protocol logic be tested without spawning a
/// process.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    string? LastError { get; }

    Task SendLineAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>Reads one line, or null when the stream closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts the MCP server as a child process and talks to it over stdin/stdout.
///
/// The command is passed to <see cref="ProcessStartInfo.ArgumentList"/>, so nothing is parsed by a
/// shell and a path with spaces or a stray quote cannot turn into an extra command. stderr is
/// drained into a small ring buffer: a server that logs a lot must not deadlock on a full pipe, and
/// its last words are the only clue when it dies.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private const int StderrTailLines = 40;

    private readonly Process _process;
    private readonly Queue<string> _stderrTail = new();
    private readonly object _sync = new();
    private bool _disposed;

    private StdioMcpTransport(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_sync)
            {
                _stderrTail.Enqueue(e.Data!);
                while (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
            }
        };
        _process.BeginErrorReadLine();
    }

    public bool IsConnected => !_disposed && !_process.HasExited;

    public string? LastError { get; private set; }

    public static StdioMcpTransport Start(McpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.HasServer)
            throw new McpTransportException("没有配置 MCP 服务器命令");

        if (!File.Exists(settings.ServerCommand))
            throw new McpTransportException($"MCP 服务器不存在：{settings.ServerCommand}");

        var startInfo = new ProcessStartInfo(settings.ServerCommand)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(settings.ServerCommand) ?? Environment.CurrentDirectory,
        };

        foreach (var argument in settings.ServerArguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = Process.Start(startInfo)
                      ?? throw new McpTransportException("MCP 服务器进程未能启动");

        return new StdioMcpTransport(process);
    }

    public async Task SendLineAsync(string json, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new McpTransportException(DescribeExit());

        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = DescribeExit();
            throw new McpTransportException(LastError, ex);
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = DescribeExit();
            throw new McpTransportException(LastError, ex);
        }
    }

    private string DescribeExit()
    {
        if (!_process.HasExited) return "MCP 服务器连接已断开";

        string tail;
        lock (_sync) tail = string.Join("\n", _stderrTail);
        return string.IsNullOrWhiteSpace(tail)
            ? $"MCP 服务器已退出（代码 {_process.ExitCode}）"
            : $"MCP 服务器已退出（代码 {_process.ExitCode}）：{tail}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1500)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            // Already gone.
        }

        _process.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
