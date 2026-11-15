namespace MyAiAssistant.Services.AgentControl;

public enum AgentControlEventType
{
    TaskEnqueued = 0,
    TaskStarted,
    TaskProgress,
    TaskPauseRequested,
    TaskPaused,
    TaskResumed,
    TaskStopRequested,
    TaskCompleted,
    TaskFailed,
    TaskCancelled,
    InteractionOpened,
    InteractionAnswered,
    InteractionWithdrawn
}

/// <summary>
/// The single notification contract. Dashboard, HUD and CLI consume the same events;
/// adding a second private event system per surface is what makes status disagree.
/// </summary>
public sealed record AgentControlEvent(
    DateTimeOffset At,
    AgentControlEventType Type,
    string? TaskId,
    string Detail);