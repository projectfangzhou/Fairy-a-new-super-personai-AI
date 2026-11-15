using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// A tool call as reported by the engine. The input is the raw engine payload; it is only
/// ever read by the classifier, never written to the audit log.
/// </summary>
public sealed record EngineToolCall(string ToolName, IReadOnlyDictionary<string, string> Input)
{
    public string? Argument(string name) =>
        Input.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What the classifier decided a tool call means, before the gate sees it.</summary>
public sealed record ClassifiedToolCall(ActionRequest Request, bool IsReadOnly);

/// <summary>
/// Why a runtime cannot be used. Reported as data instead of a silent fallback so the UI
/// can say exactly what is missing (project rule: no placeholder may look like a feature).
/// </summary>
public enum EngineAvailabilityState
{
    Ready = 0,
    NodeMissing,
    CliMissing,
    NotConfigured,
    ProbeFailed
}

public sealed record EngineAvailability(
    EngineAvailabilityState State,
    string Detail,
    string? NodePath = null,
    string? CliPath = null,
    string? Version = null,
    string? Source = null)
{
    public bool IsReady => State == EngineAvailabilityState.Ready;

    public static EngineAvailability Ready(string node, string cli, string version, string? source = null) =>
        new(EngineAvailabilityState.Ready, "Pi Agent 可用", node, cli, version, source);

    public static EngineAvailability Unusable(EngineAvailabilityState state, string detail) =>
        new(state, detail);
}

/// <summary>Runtime state of one Fairy Code session, as the UI sees it.</summary>
public sealed record FairyCodeSessionSnapshot(
    string TaskId,
    string Title,
    string Workspace,
    string Engine,
    string? Model,
    bool IsRunning,
    DateTimeOffset StartedAt,
    string? LastError);

/// <summary>Lifecycle hooks the bridge and the UI use to reach one running session.</summary>
public interface IFairyCodeSessionRegistry
{
    bool TryGet(string taskId, out FairyCodeSession session);
}

/// <summary>
/// A session that can authorize an engine tool call. The bridge depends on this seam so it
/// can be tested without spawning a real agent process.
/// </summary>
public interface IToolOracle
{
    Task<(bool Allowed, string Reason)> DecideAsync(string taskId, EngineToolCall call, CancellationToken cancellationToken);
}

/// <summary>What a Fairy Code terminal surface needs to render, in the order it happened.</summary>
public enum FairyCodeUiEventKind
{
    Opened = 0,
    PromptSent,
    AssistantText,
    ToolStart,
    ToolDecision,
    Completed,
    Failed,
    Stopped
}

public sealed record FairyCodeUiEvent(FairyCodeUiEventKind Kind, string Text, bool? Allowed = null);
