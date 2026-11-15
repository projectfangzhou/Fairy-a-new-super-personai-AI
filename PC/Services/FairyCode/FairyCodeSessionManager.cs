using System.IO;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>The model endpoint Fairy Code should use, and the key for it.</summary>
public interface IFairyCodeModelSource
{
    (EndpointSettings Endpoint, string? ApiKey)? Read();
}

public sealed record FairyCodeStartResult(bool Ok, string Message, string? TaskId = null);

/// <summary>
/// Owns the Fairy Code sessions. It is the single oracle the engine's approval bridge asks,
/// which keeps "may this tool call run?" in one place instead of inside the engine.
/// </summary>
public sealed class FairyCodeSessionManager : IToolOracle, IFairyCodeSessionRegistry, IDisposable
{
    private const string EngineName = "Pi Agent";

    private readonly AgentControlCenter _center;
    private readonly ApprovalGate _gate;
    private readonly OperationAudit _audit;
    private readonly IFairyCodeModelSource _models;
    private readonly IEngineLocator _locator;
    private readonly string _extensionPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, FairyCodeSession> _sessions = new(StringComparer.Ordinal);

    public FairyCodeSessionManager(
        AgentControlCenter center,
        ApprovalGate gate,
        OperationAudit audit,
        IFairyCodeModelSource models,
        IEngineLocator? locator = null,
        string? extensionPath = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _locator = locator ?? new PiAgentLocator();
        _extensionPath = extensionPath ?? Path.Combine(
            AppContext.BaseDirectory, "Assets", "Pi", "fairy-approval.ts");
    }

    public event Action<string>? Log;

    public string ExtensionPath => _extensionPath;

    public Task<EngineAvailability> ProbeEngineAsync(CancellationToken cancellationToken = default) =>
        _locator.ProbeAsync(cancellationToken);

    public IReadOnlyList<FairyCodeSessionSnapshot> Sessions()
    {
        lock (_sync) return _sessions.Values.Select(s => s.Snapshot()).ToArray();
    }

    public bool TryGet(string taskId, out FairyCodeSession session)
    {
        lock (_sync) return _sessions.TryGetValue(taskId, out session!);
    }

    /// <summary>
    /// Opens a session without sending a prompt, for the terminal surface where the user
    /// types the first instruction themselves. Returns the live session so the window can
    /// render its events and send further prompts into the same engine process.
    /// </summary>
    public async Task<(FairyCodeStartResult Result, FairyCodeSession? Session)> OpenAsync(
        string? workspace,
        Action<FairyCodeUiEvent>? ui = null,
        string? title = null,
        bool interactive = true,
        CancellationToken cancellationToken = default)
    {
        var root = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim();
        if (root is null || !Directory.Exists(root))
            return (new FairyCodeStartResult(false, "工作区必须是已存在的目录"), null);
        if (Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd('\\') == Path.GetFullPath(root).TrimEnd('\\'))
            return (new FairyCodeStartResult(false, "不能把磁盘根目录当作工作区"), null);

        if (!File.Exists(_extensionPath))
            return (new FairyCodeStartResult(false, $"缺少审批扩展文件：{_extensionPath}"), null);

        var availability = await ProbeEngineAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsReady)
            return (new FairyCodeStartResult(false, availability.Detail), null);

        var model = _models.Read();
        if (model is not { } source)
            return (new FairyCodeStartResult(false, "尚未配置主模型"), null);

        var binding = PiModelBindingFactory.Create(source.Endpoint, source.ApiKey);
        if (binding is null)
            return (new FairyCodeStartResult(false,
                "主模型配置不完整：需要模型名，以及可用的 Base URL（用于自定义端点）"), null);

        // The bridge token authorizes "may ask", never "may approve": the decision itself
        // still goes through the approval gate to the user.
        var bridge = new FairyCodeBridgeServer(this);
        if (!bridge.Start())
            return (new FairyCodeStartResult(false,
                "无法启动本机审批桥接（端口被占用或权限不足）"), null);

        var task = _center.Enqueue(title ?? "Fairy Code", EngineName, binding.DisplayName);
        var session = new FairyCodeSession(
            task.Id, task.Title, Path.GetFullPath(root), EngineName, binding.DisplayName,
            task, _gate, _audit, msg => Log?.Invoke(msg), ui, interactive);
        session.AuditStartup();
        lock (_sync) _sessions[task.Id] = session;

        task.Start();
        try
        {
            var process = PiAgentProcess.Start(
                new PiLaunchOptions(
                    availability.NodePath!,
                    availability.CliPath!,
                    _extensionPath,
                    Path.GetFullPath(root),
                    task.Id,
                    bridge.Url!,
                    bridge.Token,
                    binding),
                piEvent => Dispatch(session, piEvent),
                stderr => Log?.Invoke($"Fairy Code 引擎输出：{Trim(stderr, 200)}"));

            session.Attach(process);

            _ = Task.Run(async () =>
            {
                await WaitForExitAsync(process).ConfigureAwait(false);
                bridge.Stop();
                var snapshot = task.Snapshot();
                if (!snapshot.IsTerminal)
                    session.Fail($"引擎进程已退出（退出码 {process.ExitCode?.ToString() ?? "未知"}）");
            });

            return (new FairyCodeStartResult(true, "会话已就绪", task.Id), session);
        }
        catch (Exception ex)
        {
            bridge.Stop();
            session.Fail($"启动引擎失败：{ex.GetType().Name}");
            return (new FairyCodeStartResult(false, $"启动引擎失败：{ex.Message}", task.Id), session);
        }
    }

    public async Task<FairyCodeStartResult> StartAsync(
        string prompt,
        string? workspace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new FairyCodeStartResult(false, "缺少任务描述");

        var (result, session) = await OpenAsync(
            workspace, null, $"Fairy Code · {Trim(prompt, 60)}",
            interactive: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Ok || session is null) return result;

        try
        {
            await session.SendPromptAsync(prompt).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            session.Fail($"发送任务失败：{ex.GetType().Name}");
            return new FairyCodeStartResult(false, $"发送任务失败：{ex.Message}", result.TaskId);
        }
    }

    public bool Stop(string taskId)
    {
        FairyCodeSession session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(taskId, out var found)) return false;
            session = found;
        }
        session.Stop();
        return true;
    }

    public async Task<(bool Allowed, string Reason)> DecideAsync(
        string taskId, EngineToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        FairyCodeSession session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(taskId, out var found))
                return (false, "未知任务，按失败关闭拒绝");
            session = found;
        }

        var answer = await session.DecideAsync(call, cancellationToken).ConfigureAwait(false);
        if (!answer.Allowed) Log?.Invoke($"Fairy Code 工具调用被拒绝：{call.ToolName}");
        return answer;
    }

    private void Dispatch(FairyCodeSession session, PiEvent piEvent)
    {
        try { session.OnEvent(piEvent); }
        catch (Exception ex) { Log?.Invoke($"Fairy Code 事件处理失败：{ex.GetType().Name}"); }
    }

    private static async Task WaitForExitAsync(PiAgentProcess process)
    {
        // Polling a child process is sufficient here: the only job is to notice that the
        // engine died without settling, so the task does not stay "running" forever.
        for (var i = 0; i < 60 * 60 * 6; i++)
        {
            if (process.HasExited) return;
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private static string Trim(string value, int max)
    {
        var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    public void Dispose()
    {
        FairyCodeSession[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }
}
