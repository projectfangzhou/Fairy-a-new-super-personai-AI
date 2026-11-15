namespace MyAiAssistant.Services.Bots;

/// <summary>
/// How far a bot integration actually got. The UI colours the result from this instead of
/// treating "a guide was written" as a successful connection.
/// </summary>
public enum BotSetupState
{
    Ready,
    ManualStepRequired,
    NotRunning,
    Failed
}

public sealed record BotSetupResult(BotSetupState State, string Message)
{
    public bool IsReady => State == BotSetupState.Ready;
}