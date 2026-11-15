using MyAiAssistant.Services.AgentControl;

namespace MyAiAssistant.Services;

/// <summary>What a paired phone may see about this machine's work.</summary>
public sealed record PairedTaskView(
    string TaskId,
    string Title,
    string State,
    bool CanPause,
    bool CanStop,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A live approval or question. <see cref="Prompt"/> is included because a decision needs it: the
/// phone is answering the user's own pending question on the user's own machine, which is a
/// different thing from the summary feed (that one carries no content at all).
/// </summary>
public sealed record PairedPendingView(
    string InteractionId,
    string TaskId,
    string Kind,
    string Prompt,
    string? Risk,
    DateTimeOffset CreatedAt);

public sealed record PairedDeskView(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PairedTaskView> Tasks,
    IReadOnlyList<PairedPendingView> Pending);

public sealed record PairedDecision(bool Ok, string Message);

/// <summary>
/// The phone-facing side of task control: read the task list, read what is waiting for an answer,
/// and answer it.
///
/// It goes through <see cref="AgentControlCenter"/> exactly like the dashboard, so answering from the
/// phone releases the same pending item the desktop and the HUD are showing — there is no second
/// approval mechanism to disagree with the first.
///
/// Authorization is the per-device token from pairing plus an explicit switch; a device name alone
/// is not enough, and the switch being off means the phone gets nothing.
/// </summary>
public sealed class PairedTaskBridge
{
    private const int MaxTasks = 30;
    private const int MaxPromptLength = 300;

    private readonly AgentControlCenter _center;
    private readonly PairedDeviceTokens _tokens;
    private readonly Func<bool> _enabled;

    public PairedTaskBridge(AgentControlCenter center, PairedDeviceTokens tokens, Func<bool> enabled)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
    }

    /// <summary>Null when the request may proceed; otherwise the reason code to return.</summary>
    public string? RejectReason(string? deviceName, string? token) =>
        SummarySyncAccess.RejectReason(_enabled(), _tokens.Verify(deviceName, token));

    public PairedDeskView Read()
    {
        var tasks = _center.Tasks()
            .OrderByDescending(task => task.UpdatedAt)
            .Take(MaxTasks)
            .Select(task => new PairedTaskView(task.Id, Scrub(task.Title), task.State.ToString(),
                task.CanPause, task.CanStop, task.UpdatedAt))
            .ToArray();

        var pending = _center.Pending()
            .OrderBy(item => item.CreatedAt)
            .Select(item => new PairedPendingView(
                item.Id,
                item.TaskId,
                item.Kind.ToString(),
                // The prompt is truncated but not path-scrubbed: the user is being asked to decide on
                // their own action, and "写入（路径已省略）" would leave them approving blind.
                Truncate(item.Prompt, MaxPromptLength),
                item.Risk?.ToString(),
                item.CreatedAt))
            .ToArray();

        return new PairedDeskView(DateTimeOffset.UtcNow, tasks, pending);
    }

    /// <summary>Answers an approval. Unknown or already-answered interactions are reported, not ignored.</summary>
    public PairedDecision Answer(string? interactionId, bool approved)
    {
        if (string.IsNullOrWhiteSpace(interactionId))
            return new PairedDecision(false, "缺少待决项 id");

        return _center.Answer(interactionId, approved)
            ? new PairedDecision(true, approved ? "已允许" : "已拒绝")
            : new PairedDecision(false, "该待决项已不存在（可能已在别处回答）");
    }

    public PairedDecision AnswerText(string? interactionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(interactionId))
            return new PairedDecision(false, "缺少待决项 id");
        if (string.IsNullOrWhiteSpace(text))
            return new PairedDecision(false, "回答内容为空");

        return _center.AnswerText(interactionId, text)
            ? new PairedDecision(true, "已回答")
            : new PairedDecision(false, "该待决项已不存在（可能已在别处回答）");
    }

    /// <summary>
    /// Task titles and prompts can contain paths; the phone notification/list is the wrong place for
    /// a full path, but the user still needs enough to recognise the item.
    /// </summary>
    private static string Scrub(string? value, int max = 40)
    {
        var text = Notifications.SystemNotificationPolicy.Scrub(value, max);
        return string.IsNullOrWhiteSpace(text) ? "（无标题）" : text;
    }

    private static string Truncate(string? value, int max)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
