using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services;

public sealed record HarnessRequest(string Executable, string Workspace, string Prompt, string? CliEntryPoint = null);
public sealed record HarnessConsent(string Executable, string Workspace, string Endpoint, string Model, string? CliEntryPoint = null);

public sealed class DeepSeekHarnessService
{
    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("模型端点必须为 HTTPS（本机可用 HTTP），且不能包含凭据、查询参数或片段。");
        var path = uri.AbsolutePath.TrimEnd('/');
        const string suffix = "/chat/completions";
        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            path = path[..^suffix.Length];
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    public static string CreatePatch(string endpoint, string model, string workspace)
    {
        var rows = new List<object>();
        foreach (var id in new[] { "llm-deepseek", "session-log-deepseek", "persistent-bash",
                     "persistent-pwsh", "terminal-bash", "terminal-pwsh", "mcp-resources" })
            rows.Add(new { id, disabled = true });
        rows.Add(new { id = "sandbox-policy", config = new { mode = "read-only", workspaceRoot = workspace } });
        rows.Add(new
        {
            insert = new[]
            {
                new
                {
                    id = "llm-pi-ai", name = "@deepseek-ai/dsh-llm-pi-ai",
                    config = new
                    {
                        providers = new Dictionary<string, object>
                        {
                            ["fairy-gateway"] = new
                            {
                                api = "openai-completions", apiKeyEnv = "FAIRY_DSH_API_KEY",
                                baseURL = endpoint,
                                compat = new { supportsDeveloperRole = false, maxTokensField = "max_tokens" },
                                models = new[] { new { id = model } }
                            }
                        }
                    }
                }
            }
        });
        return JsonSerializer.Serialize(rows);
    }

    public static ProcessStartInfo CreateStartInfo(HarnessRequest request, string patch)
    {
        if (!Path.IsPathFullyQualified(request.Executable) || !File.Exists(request.Executable))
            throw new ArgumentException("请选择已安装运行时的完整路径。");
        var executable = Path.GetFullPath(request.Executable);
        var isNode = request.CliEntryPoint is not null;
        var expectedName = isNode ? "node.exe" : "dsh.exe";
        if (!string.Equals(Path.GetFileName(executable), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"请选择 {expectedName}；不支持命令字符串、cmd 或源码启动器。");
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace))
            throw new ArgumentException("请选择已存在的绝对工作目录。");
        string? entry = null;
        if (isNode)
        {
            entry = request.CliEntryPoint!;
            if (!Path.IsPathFullyQualified(entry) || !File.Exists(entry) ||
                !string.Equals(Path.GetFileName(entry), "bin.js", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(entry)), "lib", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Node 模式需要已构建 dsh 安装的 lib/bin.js 完整路径，不支持 TypeScript 源码。");
            entry = Path.GetFullPath(entry);
        }
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(request.Workspace), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (entry is not null) start.ArgumentList.Add(entry);
        foreach (var arg in new[] { "--profile", "sdk-minimal", "--patch", patch }) start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<string> RunAsync(HarnessRequest request,
        Func<HarnessConsent, CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        var home = Path.Combine(Path.GetTempPath(), "FairyHarness", Guid.NewGuid().ToString("N"));
        var patch = Path.Combine(home, "fairy.patch.yml");
        var start = CreateStartInfo(request, patch);
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("请输入任务。");
        var llm = ConfigManager.Load().LLM;
        var endpoint = NormalizeBaseUrl(llm.BaseUrl);
        var model = llm.Model.Trim();
        var key = llm.ApiKey;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("请先在仙灵模型设置中填写模型名称和 API Key。");
        var workspace = Path.GetFullPath(request.Workspace);
        var executable = Path.GetFullPath(request.Executable);
        if (!await confirm(new(executable, workspace, endpoint, model,
                request.CliEntryPoint is null ? null : Path.GetFullPath(request.CliEntryPoint)), cancellationToken))
            throw new OperationCanceledException(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(patch, CreatePatch(endpoint, model, workspace), new UTF8Encoding(false), cancellationToken);
            // Do not inherit ambient Harness configuration, debug switches or provider credentials.
            var inherited = start.Environment.ToArray();
            foreach (var entry in inherited)
                if (entry.Key.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase) ||
                    entry.Key.EndsWith("API_KEY", StringComparison.OrdinalIgnoreCase) ||
                    entry.Key is "NODE_OPTIONS" or "NODE_DEBUG" or "DEBUG")
                    start.Environment.Remove(entry.Key);
            start.Environment["DSH_HOME"] = home;
            start.Environment["DSH_TELEMETRY_DISABLED"] = "1";
            start.Environment["DSH_SYSTEM_PROMPT"] = "You are Fairy's planning assistant. No tools are available. Answer from the user's supplied text. Do not claim to inspect files or execute commands.";
            start.Environment["FAIRY_DSH_API_KEY"] = key;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("无法启动 dsh。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            using var killOnCancel = deadline.Token.Register(() => Kill(process));
            var stderr = DrainAsync(process.StandardError);
            try
            {
                var sessionId = "fairy-" + Guid.NewGuid().ToString("N");
                await SendAsync(process, 1, "initialize", new { cwd = workspace, provider = "fairy-gateway", model }, deadline.Token);
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(45));
                    await ResponseAsync(process, 1, startup.Token);
                }
                await SendAsync(process, 2, "session/prompt", new
                {
                    sessionId, contentBlocks = new[] { new { type = "text", text = request.Prompt } }
                }, deadline.Token);
                var output = new StringBuilder();
                var accepted = false;
                var completed = false;
                while (!accepted || !completed)
                {
                    using var frame = await ReadFrameAsync(process, deadline.Token);
                    var root = frame.RootElement;
                    RejectError(root);
                    if (root.TryGetProperty("id", out var id))
                    {
                        if (root.TryGetProperty("method", out _))
                            throw new InvalidOperationException("dsh 请求了未支持的交互权限，已停止；本入口禁止工具执行。");
                        if (id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2 && root.TryGetProperty("result", out _)) accepted = true;
                        continue;
                    }
                    if (!root.TryGetProperty("method", out var method) || method.GetString() != "session.event") continue;
                    var parameters = root.GetProperty("params");
                    if (parameters.GetProperty("sessionId").GetString() != sessionId) continue;
                    var ev = parameters.GetProperty("event");
                    var type = ev.GetProperty("type").GetString();
                    var data = ev.GetProperty("data");
                    if (type == "tool/call") throw new InvalidOperationException("运行时违反无工具配置，已停止。请检查 dsh 版本。");
                    if (type == "assistant/message")
                    {
                        foreach (var block in data.GetProperty("message").GetProperty("content").EnumerateArray())
                            if (block.GetProperty("type").GetString() == "text") output.Append(block.GetProperty("text").GetString());
                        output.AppendLine();
                        if (output.Length > 1_000_000) throw new InvalidOperationException("dsh 回复超过显示上限，已停止。");
                    }
                    if (type == "turn/end")
                    {
                        if (data.GetProperty("reason").GetProperty("kind").GetString() != "completed")
                            throw new InvalidOperationException("dsh 本轮未正常完成（取消、模型错误或输出限制）。");
                        completed = true;
                    }
                }
                await SendAsync(process, 3, "shutdown", new { }, deadline.Token);
                using (var shutdown = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                {
                    shutdown.CancelAfter(TimeSpan.FromSeconds(10));
                    await ResponseAsync(process, 3, shutdown.Token);
                    await process.WaitForExitAsync(shutdown.Token);
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("dsh 退出异常。");
                if (output.Length == 0) throw new InvalidOperationException("dsh 未返回可显示的文本。");
                return output.ToString().Replace(key, "[REDACTED]", StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("dsh 启动、运行或关闭超时，子进程已停止。请确认运行时包含 llm-pi-ai 插件。");
            }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync();
                await stderr;
            }
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); }
            catch (IOException) { /* Runtime may briefly retain session files after termination. */ }
            catch (UnauthorizedAccessException) { /* Leave only this isolated run's non-credential files. */ }
        }
    }

    private static async Task SendAsync(Process process, int id, string method, object parameters, CancellationToken token)
    {
        var text = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await process.StandardInput.WriteLineAsync(text.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    private static async Task<JsonDocument> ReadFrameAsync(Process process, CancellationToken token)
    {
        var line = await process.StandardOutput.ReadLineAsync(token);
        if (line is null) throw new InvalidOperationException("dsh 连接已关闭。请确认安装的运行时支持 sdk-minimal 和 llm-pi-ai。");
        if (line.Length > 4_000_000) throw new InvalidOperationException("dsh 协议帧过大。");
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { throw new InvalidOperationException("dsh 输出不是受支持的 JSON-RPC 协议。"); }
    }

    private static void RejectError(JsonElement frame)
    {
        // Runtime diagnostics can echo credentials; never surface raw frames or stderr.
        if (frame.TryGetProperty("error", out _))
            throw new InvalidOperationException("dsh 拒绝了请求。请检查运行时版本、模型配置以及 llm-pi-ai 插件是否可用。");
    }

    private static async Task ResponseAsync(Process process, int expected, CancellationToken token)
    {
        while (true)
        {
            using var frame = await ReadFrameAsync(process, token);
            var root = frame.RootElement;
            RejectError(root);
            if (root.TryGetProperty("id", out var id))
            {
                if (root.TryGetProperty("method", out _)) throw new InvalidOperationException("dsh 请求了未支持的交互权限。");
                if (id.ValueKind == JsonValueKind.Number && id.GetInt32() == expected && root.TryGetProperty("result", out var result))
                {
                    if (expected == 1 && (!result.TryGetProperty("serverInfo", out var info) ||
                        !info.TryGetProperty("name", out var name) || name.GetString() != "deepseek-harness-sdk-runtime"))
                        throw new InvalidOperationException("dsh 运行时身份与受支持的 SDK 协议不匹配。");
                    return;
                }
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Already exited. */ }
    }
}
