using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.FairyCode;

public enum PiEventKind
{
    Text = 0,
    ToolStart,
    Settled,
    Error,
    Response,
    Other
}

public sealed record PiEvent(PiEventKind Kind, string? Text = null, string? ToolName = null, string? Detail = null);

/// <summary>
/// Parses the engine's JSONL event stream. Kept separate from the process so the mapping is
/// unit-testable without spawning anything.
/// </summary>
public static class PiRpcParser
{
    public static PiEvent Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new PiEvent(PiEventKind.Other);

        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return new PiEvent(PiEventKind.Other); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new PiEvent(PiEventKind.Other);
            var type = ReadString(root, "type");

            switch (type)
            {
                case "message_update":
                    return ParseMessageUpdate(root);
                case "tool_execution_start":
                    return new PiEvent(PiEventKind.ToolStart, ToolName: ReadString(root, "toolName"));
                case "agent_settled":
                    return new PiEvent(PiEventKind.Settled);
                case "extension_error":
                    return new PiEvent(PiEventKind.Error, Detail: ReadString(root, "error")
                        ?? ReadString(root, "message") ?? "扩展报错");
                case "response":
                    return new PiEvent(PiEventKind.Response,
                        Detail: ReadString(root, "command"),
                        Text: root.TryGetProperty("success", out var success) &&
                              success.ValueKind == JsonValueKind.False
                            ? ReadString(root, "error") ?? "命令失败"
                            : null);
                default:
                    return new PiEvent(PiEventKind.Other, Detail: type);
            }
        }
    }

    private static PiEvent ParseMessageUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("assistantMessageEvent", out var update) ||
            update.ValueKind != JsonValueKind.Object)
            return new PiEvent(PiEventKind.Other);

        return ReadString(update, "type") switch
        {
            "text_delta" => new PiEvent(PiEventKind.Text, ReadString(update, "delta")),
            "toolcall_start" => new PiEvent(PiEventKind.ToolStart, ToolName: ReadString(update, "toolName")),
            _ => new PiEvent(PiEventKind.Other)
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record PiLaunchOptions(
    string NodePath,
    string CliPath,
    string ExtensionPath,
    string Workspace,
    string TaskId,
    string BridgeUrl,
    string BridgeToken,
    PiModelBinding Model);

/// <summary>
/// One engine process in RPC mode. The process never runs a shell, arguments are passed as a
/// list, and the model key travels through the environment only.
/// </summary>
public sealed class PiAgentProcess : IDisposable
{
    private readonly Process _process;
    private readonly Action<PiEvent> _onEvent;
    private readonly Action<string>? _onStderr;
    private readonly object _writeLock = new();
    private bool _disposed;

    private PiAgentProcess(Process process, Action<PiEvent> onEvent, Action<string>? onStderr)
    {
        _process = process;
        _onEvent = onEvent;
        _onStderr = onStderr;
    }

    public static PiAgentProcess Start(PiLaunchOptions options, Action<PiEvent> onEvent, Action<string>? onStderr = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onEvent);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.NodePath,
            WorkingDirectory = options.Workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(options.CliPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        // No session transcript: it would store file contents and conversation on disk.
        startInfo.ArgumentList.Add("--no-session");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(options.ExtensionPath);
        if (!string.IsNullOrWhiteSpace(options.Model.ProviderArgument))
        {
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(options.Model.ProviderArgument!);
        }
        if (!string.IsNullOrWhiteSpace(options.Model.ModelArgument))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(options.Model.ModelArgument!);
        }

        foreach (var pair in options.Model.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

        startInfo.Environment["FAIRY_BRIDGE_URL"] = options.BridgeUrl;
        startInfo.Environment["FAIRY_BRIDGE_TOKEN"] = options.BridgeToken;
        startInfo.Environment["FAIRY_TASK_ID"] = options.TaskId;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Pi Agent 进程");

        var instance = new PiAgentProcess(process, onEvent, onStderr);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try { instance._onEvent(PiRpcParser.Parse(e.Data)); }
            catch (Exception) { /* a malformed event line must not stop the session */ }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) instance._onStderr?.Invoke(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return instance;
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public int? ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : null; } catch (InvalidOperationException) { return null; } }
    }

    public Task SendPromptAsync(string message)
    {
        // No streamingBehavior: a followUp/steer flag is only meaningful while the agent is
        // mid-stream, and passing it on an idle session is an error in the protocol.
        var payload = new { type = "prompt", message };
        return WriteAsync(JsonSerializer.Serialize(payload));
    }

    public Task AbortAsync() => WriteAsync("""{"type":"abort"}""");

    /// <summary>
    /// Sends a protocol line verbatim. Used for state probes and for anything the typed
    /// helpers do not cover yet; callers are responsible for valid JSON.
    /// </summary>
    public Task SendCommandAsync(string json) => WriteAsync(json);

    private async Task WriteAsync(string line)
    {
        string? failure = null;
        lock (_writeLock)
        {
            if (_disposed || HasExited) failure = "引擎进程已结束";
            else
            {
                try
                {
                    _process.StandardInput.WriteLine(line);
                    _process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    failure = $"写入引擎失败：{ex.GetType().Name}";
                }
            }
        }

        if (failure is not null) throw new InvalidOperationException(failure);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); } catch (Exception) { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (Exception) { /* already gone */ }
        _process.Dispose();
    }
}
