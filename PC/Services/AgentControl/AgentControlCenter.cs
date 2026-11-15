using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.AgentControl;

/// <summary>
/// Owns every agent task and every open interaction. All surfaces read state from here,
/// which is what keeps Dashboard, HUD and CLI from disagreeing about a task.
/// </summary>
public sealed class AgentControlCenter
{
    private const int MaxEvents = 500;
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentTaskHandle> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingInteraction> _pending = new(StringComparer.Ordinal);
    private readonly List<AgentControlEvent> _events = new();

    public event Action<AgentControlEvent>? Changed;

    public AgentTaskHandle Enqueue(string title, string? engine = null, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var handle = new AgentTaskHandle(this, Guid.NewGuid().ToString("N")[..12], title, engine, model);
        lock (_sync) _tasks[handle.Id] = handle;
        Publish(AgentControlEventType.TaskEnqueued, handle.Id, title);
        return handle;
    }

    public IReadOnlyList<AgentTaskSnapshot> Tasks()
    {
        AgentTaskHandle[] handles;
        lock (_sync) handles = _tasks.Values.ToArray();
        return handles.Select(h => h.Snapshot()).OrderBy(s => s.CreatedAt).ToArray();
    }

    public AgentTaskSnapshot? Task(string id)
    {
        lock (_sync) return _tasks.TryGetValue(id, out var handle) ? handle.Snapshot() : null;
    }

    public IReadOnlyList<PendingInteractionSnapshot> Pending()
    {
        lock (_sync)
            return _pending.Values
                .Select(p => new PendingInteractionSnapshot(p.Id, p.TaskId, p.Kind, p.Prompt, p.Risk, p.CreatedAt))
                .OrderBy(p => p.CreatedAt)
                .ToArray();
    }

    public IReadOnlyList<AgentControlEvent> Events(int max = 100)
    {
        lock (_sync)
        {
            var take = Math.Clamp(max, 1, MaxEvents);
            return _events.Count <= take ? _events.ToArray() : _events.Skip(_events.Count - take).ToArray();
        }
    }

    public bool RequestPause(string taskId) => WithTask(taskId, h => h.TryRequestPause());

    public bool RequestResume(string taskId) => WithTask(taskId, h => h.TryRequestResume());

    public bool RequestStop(string taskId) => WithTask(taskId, h => h.RequestStop());

    public bool Answer(string interactionId, bool approved)
    {
        if (!TryTakeInteraction(interactionId, out var interaction)) return false;
        interaction.Completion.TrySetResult(approved ? InteractionResult.Allow : InteractionResult.Deny);
        Publish(AgentControlEventType.InteractionAnswered, interaction.TaskId, approved ? "用户已批准" : "用户已拒绝");
        return true;
    }

    public bool AnswerText(string interactionId, string text)
    {
        if (!TryTakeInteraction(interactionId, out var interaction)) return false;
        interaction.Completion.TrySetResult(new InteractionResult(true, text));
        Publish(AgentControlEventType.InteractionAnswered, interaction.TaskId, "用户已回答");
        return true;
    }

    internal PendingInteraction OpenInteraction(string taskId, PendingKind kind, string prompt, RiskCategory? risk)
    {
        var interaction = new PendingInteraction(Guid.NewGuid().ToString("N")[..12], taskId, kind, prompt, risk);
        lock (_sync) _pending[interaction.Id] = interaction;
        Publish(AgentControlEventType.InteractionOpened, taskId, $"{kind}：{prompt}");
        return interaction;
    }

    internal void CloseInteraction(string interactionId, string detail)
    {
        var removed = false;
        lock (_sync) removed = _pending.Remove(interactionId);
        if (removed) Publish(AgentControlEventType.InteractionWithdrawn, null, detail);
    }

    private bool TryTakeInteraction(string interactionId, [NotNullWhen(true)] out PendingInteraction? interaction)
    {
        lock (_sync) return _pending.Remove(interactionId, out interaction);
    }

    private bool WithTask(string taskId, Func<AgentTaskHandle, bool> action)
    {
        AgentTaskHandle? handle;
        lock (_sync) _tasks.TryGetValue(taskId, out handle);
        return handle is not null && action(handle);
    }

    internal void Publish(AgentControlEventType type, string? taskId, string detail)
    {
        var controlEvent = new AgentControlEvent(DateTimeOffset.UtcNow, type, taskId, detail);
        lock (_sync)
        {
            _events.Add(controlEvent);
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        var handler = Changed;
        if (handler is null) return;
        try { handler(controlEvent); }
        catch (Exception ex) { Debug.WriteLine($"agent control listener failed: {ex.Message}"); }
    }
}

internal sealed record InteractionResult(bool Approved, string? Text = null)
{
    public static readonly InteractionResult Allow = new(true);
    public static readonly InteractionResult Deny = new(false);
}

internal sealed class PendingInteraction(
    string id, string taskId, PendingKind kind, string prompt, RiskCategory? risk)
{
    public string Id { get; } = id;
    public string TaskId { get; } = taskId;
    public PendingKind Kind { get; } = kind;
    public string Prompt { get; } = prompt;
    public RiskCategory? Risk { get; } = risk;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public TaskCompletionSource<InteractionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}