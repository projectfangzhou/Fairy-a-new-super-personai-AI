using System.Threading;
using System.Threading.Tasks;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.AgentControl;

/// <summary>
/// The task's own view of its lifecycle. A long-running loop is expected to call
/// <see cref="WaitIfPausedAsync"/> between atomic operations and
/// <see cref="ThrowIfStopped"/> before each action, so that pausing takes effect only on
/// a safe boundary and stopping can never be continued.
/// </summary>
public sealed class AgentTaskHandle
{
    private readonly AgentControlCenter _center;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private AgentTaskState _state = AgentTaskState.Queued;
    private bool _pauseRequested;
    private TaskCompletionSource<bool>? _resume;
    private int _step;
    private int _total;
    private string? _stepLabel;
    private string? _error;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    internal AgentTaskHandle(AgentControlCenter center, string id, string title, string? engine, string? model)
    {
        _center = center;
        Id = id;
        Title = title;
        Engine = engine;
        Model = model;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Engine { get; }
    public string? Model { get; }

    public CancellationToken StopToken => _stop.Token;

    public AgentTaskState State
    {
        get { lock (_sync) return _state; }
    }

    public bool Start()
    {
        lock (_sync)
        {
            if (_state != AgentTaskState.Queued) return false;
            _state = AgentTaskState.Running;
            _updatedAt = DateTimeOffset.UtcNow;
        }
        _center.Publish(AgentControlEventType.TaskStarted, Id, Title);
        return true;
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        TaskCompletionSource<bool> resume;
        lock (_sync)
        {
            if (!_pauseRequested || _state != AgentTaskState.Running) return;
            _state = AgentTaskState.Paused;
            _updatedAt = DateTimeOffset.UtcNow;
            resume = _resume ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _center.Publish(AgentControlEventType.TaskPaused, Id, "已在操作边界暂停");

        using var registration = cancellationToken.Register(() => resume.TrySetResult(false));
        await resume.Task.ConfigureAwait(false);
        ThrowIfStopped();
    }

    public void ReportProgress(int step, int total, string? label = null)
    {
        lock (_sync)
        {
            if (IsTerminal(_state)) return;
            _step = step;
            _total = total;
            _stepLabel = label;
            _updatedAt = DateTimeOffset.UtcNow;
        }
        _center.Publish(AgentControlEventType.TaskProgress, Id, label ?? $"{step}/{total}");
    }

    public bool Complete()
    {
        lock (_sync)
        {
            if (IsTerminal(_state)) return false;
            _state = AgentTaskState.Completed;
            _updatedAt = DateTimeOffset.UtcNow;
            _resume?.TrySetResult(false);
            _resume = null;
        }
        _center.Publish(AgentControlEventType.TaskCompleted, Id, Title);
        return true;
    }

    public bool Fail(string error)
    {
        lock (_sync)
        {
            if (IsTerminal(_state)) return false;
            _state = AgentTaskState.Failed;
            _error = error;
            _updatedAt = DateTimeOffset.UtcNow;
            _resume?.TrySetResult(false);
            _resume = null;
        }
        _center.Publish(AgentControlEventType.TaskFailed, Id, error);
        return true;
    }

    public void ThrowIfStopped()
    {
        if (_stop.IsCancellationRequested) throw new OperationCanceledException(_stop.Token);
    }

    public async Task<bool> AskApprovalAsync(string prompt, RiskCategory risk, CancellationToken cancellationToken = default)
    {
        var result = await AskAsync(PendingKind.Approval, prompt, risk, cancellationToken).ConfigureAwait(false);
        return result?.Approved == true;
    }

    public async Task<string?> AskQuestionAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var result = await AskAsync(PendingKind.Question, prompt, null, cancellationToken).ConfigureAwait(false);
        return result?.Text;
    }

    public AgentTaskSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new AgentTaskSnapshot(
                Id, Title, _state, _pauseRequested, Engine, Model,
                _step, _total, _stepLabel, _createdAt, _updatedAt, _error);
        }
    }

    internal bool TryRequestPause()
    {
        lock (_sync)
        {
            if (IsTerminal(_state) || _state != AgentTaskState.Running || _pauseRequested) return false;
            _pauseRequested = true;
            _updatedAt = DateTimeOffset.UtcNow;
        }
        _center.Publish(AgentControlEventType.TaskPauseRequested, Id, "将在当前操作完成后暂停");
        return true;
    }

    internal bool TryRequestResume()
    {
        TaskCompletionSource<bool>? resume;
        lock (_sync)
        {
            if (_state != AgentTaskState.Paused) return false;
            _pauseRequested = false;
            _state = AgentTaskState.Running;
            _updatedAt = DateTimeOffset.UtcNow;
            resume = _resume;
            _resume = null;
        }
        _center.Publish(AgentControlEventType.TaskResumed, Id, "已继续");
        resume?.TrySetResult(true);
        return true;
    }

    internal bool RequestStop()
    {
        TaskCompletionSource<bool>? resume;
        lock (_sync)
        {
            if (IsTerminal(_state)) return false;
            _state = AgentTaskState.Cancelled;
            _updatedAt = DateTimeOffset.UtcNow;
            resume = _resume;
            _resume = null;
            _pauseRequested = false;
        }

        _center.Publish(AgentControlEventType.TaskStopRequested, Id, "已请求停止");
        _stop.Cancel();
        resume?.TrySetResult(false);
        _center.Publish(AgentControlEventType.TaskCancelled, Id, Title);
        return true;
    }

    private async Task<InteractionResult?> AskAsync(
        PendingKind kind, string prompt, RiskCategory? risk, CancellationToken cancellationToken)
    {
        ThrowIfStopped();
        var interaction = _center.OpenInteraction(Id, kind, prompt, risk);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, StopToken);
        try
        {
            using var registration = linked.Token.Register(() => interaction.Completion.TrySetCanceled(linked.Token));
            return await interaction.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfStopped();
            throw;
        }
        finally
        {
            _center.CloseInteraction(interaction.Id, $"{kind} 未决项已关闭");
        }
    }

    private static bool IsTerminal(AgentTaskState state) =>
        state is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Cancelled;
}