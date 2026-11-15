using System.IO;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;
namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// One running Fairy Code task: an engine process plus the authorization path its tool calls
/// must pass. The engine is never trusted to police itself — every tool call it proposes
/// reaches <see cref="DecideAsync"/> first, and the answer there comes from the same
/// approval gate the rest of the app uses.
/// </summary>
public sealed class FairyCodeSession : IDisposable
{
    private readonly ApprovalGate _gate;
    private readonly OperationAudit _audit;
    private readonly FairyToolClassifier _classifier;
    private readonly Action<string> _log;
    private readonly Action<FairyCodeUiEvent>? _ui;
    private readonly bool _interactive;
    private readonly object _sync = new();
    private PiAgentProcess? _process;
    private int _toolSteps;
    private bool _disposed;

    public FairyCodeSession(
        string taskId,
        string title,
        string workspace,
        string engine,
        string? model,
        AgentTaskHandle handle,
        ApprovalGate gate,
        OperationAudit audit,
        Action<string> log,
        Action<FairyCodeUiEvent>? ui = null,
        bool interactive = false)
    {
        TaskId = taskId;
        Title = title;
        Workspace = workspace;
        Engine = engine;
        Model = model;
        Handle = handle;
        StartedAt = DateTimeOffset.UtcNow;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ui = ui;
        _interactive = interactive;
        _classifier = new FairyToolClassifier(workspace);
    }

    public string TaskId { get; }
    public string Title { get; }
    public string Workspace { get; }
    public string Engine { get; }
    public string? Model { get; }
    public AgentTaskHandle Handle { get; }
    public DateTimeOffset StartedAt { get; }
    public string? LastError { get; private set; }
    public string Transcript { get; private set; } = string.Empty;

    public bool IsRunning =>
        !Handle.Snapshot().IsTerminal && _process is not null && !_process.HasExited;

    public FairyCodeSessionSnapshot Snapshot() => new(
        TaskId, Title, Workspace, Engine, Model, IsRunning, StartedAt, LastError);

    public void Attach(PiAgentProcess process) => _process = process;

    /// <summary>
    /// Authorizes one tool call. Pausing is enforced here, because a tool call is exactly the
    /// boundary between two atomic operations: while paused, the engine blocks before the
    /// next call instead of finishing it and stopping afterwards.
    /// </summary>
    public async Task<(bool Allowed, string Reason)> DecideAsync(
        EngineToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (Handle.Snapshot().IsTerminal)
            return (false, "任务已结束，不再执行任何工具调用");

        try
        {
            await Handle.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, "任务已停止");
        }

        var classified = _classifier.Classify(call);
        if (classified.IsReadOnly && classified.Request.Category == RiskCategory.None)
        {
            Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.ToolDecision,
                $"只读放行：{call.ToolName} {classified.Request.Target}".TrimEnd(), true));
            return (true, "只读操作");
        }

        // The prompt carries the change preview for file mutations: approving "write to X"
        // without seeing what changes is a guess, not consent.
        var prompt = BuildApprovalPrompt(classified.Request, call);
        Func<ActionRequest, CancellationToken, Task<bool>> approver =
            (request, ct) => Handle.AskApprovalAsync(prompt, request.Category, ct);

        var result = await _gate
            .AuthorizeAsync(classified.Request, cancellationToken, approver)
            .ConfigureAwait(false);

        Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.ToolDecision,
            $"{call.ToolName}：{result.Reason}（{result.Decision}）", result.IsAllowed));

        if (!result.IsAllowed)
        {
            _log($"Fairy Code 拒绝 {call.ToolName}：{result.Decision} {result.Reason}");
            return (false, $"{result.Reason}（{result.Decision}）");
        }

        return (true, result.Reason);
    }

    /// <summary>
    /// What the user actually reads before answering. The summary and target come from the
    /// classifier; the preview comes from the file pair itself.
    /// </summary>
    internal string BuildApprovalPrompt(ActionRequest request, EngineToolCall call)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(request.Summary);
        if (!string.IsNullOrWhiteSpace(request.Target))
            builder.AppendLine($"目标：{request.Target}");

        var preview = FairyDiffPreview.Build(call, Workspace);
        if (preview is not null)
        {
            builder.AppendLine();
            builder.AppendLine("—— 变更预览 ——");
            builder.Append(preview.Text);
        }

        return builder.ToString().TrimEnd();
    }

    public Task SendPromptAsync(string prompt)
    {
        var process = _process ?? throw new InvalidOperationException("引擎尚未启动");
        Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.PromptSent, prompt.Trim()));
        return process.SendPromptAsync(prompt);
    }

    public void OnEvent(PiEvent piEvent)
    {
        switch (piEvent.Kind)
        {
            case PiEventKind.Text when !string.IsNullOrEmpty(piEvent.Text):
                if (Transcript.Length < 20_000) Transcript += piEvent.Text;
                Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.AssistantText, piEvent.Text));
                break;
            case PiEventKind.ToolStart:
                _toolSteps++;
                Handle.ReportProgress(_toolSteps, _toolSteps, $"工具：{piEvent.ToolName ?? "未知"}");
                Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.ToolStart, piEvent.ToolName ?? "未知"));
                break;
            case PiEventKind.Settled:
                // A terminal session is one long-lived task: the turn ended, the session did
                // not. Completing it would also make every later tool call look like it came
                // from a finished task.
                if (_interactive)
                {
                    Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.Completed, "本轮结束"));
                }
                else
                {
                    Complete();
                }
                break;
            case PiEventKind.Error:
                LastError = piEvent.Detail;
                Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.Failed, piEvent.Detail ?? "引擎扩展报错"));
                Handle.Fail(piEvent.Detail ?? "引擎扩展报错");
                _log($"Fairy Code 引擎报错：{piEvent.Detail}");
                break;
        }
    }

    public void Complete()
    {
        if (Handle.Complete())
        {
            Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.Completed, "本轮结束"));
            _log($"Fairy Code 任务完成：{TaskId}");
        }
    }

    public void Fail(string error)
    {
        LastError = error;
        if (Handle.Fail(error))
        {
            Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.Failed, error));
            _log($"Fairy Code 任务失败：{error}");
        }
    }

    private void Emit(FairyCodeUiEvent uiEvent)
    {
        try { _ui?.Invoke(uiEvent); }
        catch (Exception ex) { _log($"Fairy Code 界面事件失败：{ex.GetType().Name}"); }
    }

    public void Stop()
    {
        Handle.RequestStop();
        Emit(new FairyCodeUiEvent(FairyCodeUiEventKind.Stopped, "任务已停止"));
        var process = _process;
        if (process is null) return;
        try { process.AbortAsync().GetAwaiter().GetResult(); } catch (Exception) { }
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var process = _process;
        _process = null;
        process?.Dispose();
    }

    public void AuditStartup()
    {
        // Reading the configured model key to start an engine is a credential use, so it is
        // recorded even though the user started it: the audit is for later reconstruction.
        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "fairycode.start",
            RiskCategory.ReadWriteCredentials,
            "启动 Fairy Code 引擎",
            Workspace,
            "allowed",
            "用户在工作区启动任务，引擎使用已配置的模型密钥",
            "user",
            0));
    }
}
