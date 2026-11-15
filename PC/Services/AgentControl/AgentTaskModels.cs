using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.AgentControl;

public enum AgentTaskState
{
    Queued = 0,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum PendingKind
{
    Approval = 0,
    Question
}

/// <summary>
/// Immutable view of a task. The UI, the HUD and the CLI all render from this shape so a
/// task never looks different in two places.
/// </summary>
public sealed record AgentTaskSnapshot(
    string Id,
    string Title,
    AgentTaskState State,
    bool PauseRequested,
    string? Engine,
    string? Model,
    int Step,
    int TotalSteps,
    string? StepLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error)
{
    public bool IsTerminal => State is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Cancelled;

    public bool CanPause => State == AgentTaskState.Running && !PauseRequested;

    public bool CanResume => State == AgentTaskState.Paused;

    public bool CanStop => !IsTerminal;
}

public sealed record PendingInteractionSnapshot(
    string Id,
    string TaskId,
    PendingKind Kind,
    string Prompt,
    RiskCategory? Risk,
    DateTimeOffset CreatedAt);