using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.Notifications;

/// <summary>
/// Turns agent events into notifications.
///
/// It listens to the same event stream the control centre and the HUD already read, so there is no
/// second source of truth: an approval that shows up in the dashboard and the one that shows up in
/// the HUD are the same pending item, and answering either clears both.
/// </summary>
public sealed class NotificationBridge : IDisposable
{
    private readonly AgentControlCenter _center;
    private readonly NotificationCenter _notifications;
    private readonly HashSet<string> _seenInteractions = new(StringComparer.Ordinal);
    private bool _disposed;

    public NotificationBridge(AgentControlCenter center, NotificationCenter notifications)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _center.Changed += OnChanged;
    }

    private void OnChanged(AgentControlEvent controlEvent)
    {
        if (_disposed) return;

        try
        {
            switch (controlEvent.Type)
            {
                case AgentControlEventType.InteractionOpened:
                    NotifyPending(controlEvent.TaskId);
                    break;

                case AgentControlEventType.InteractionAnswered:
                case AgentControlEventType.InteractionWithdrawn:
                    // Whatever is no longer pending must stop looking actionable.
                    _notifications.MarkResolvedInteractions(
                        _center.Pending().Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
                    break;

                case AgentControlEventType.TaskCompleted:
                    NotifyTask(controlEvent.TaskId, NotificationKind.TaskCompleted,
                        NotificationSeverity.Success, "任务完成", "已经做完，可以看结果了");
                    break;

                case AgentControlEventType.TaskFailed:
                    NotifyTask(controlEvent.TaskId, NotificationKind.TaskFailed,
                        NotificationSeverity.Error, "任务失败", controlEvent.Detail);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A notification that cannot be built must never take down the event publisher.
        }
    }

    private void NotifyPending(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        foreach (var pending in _center.Pending().Where(item => item.TaskId == taskId))
        {
            if (!_seenInteractions.Add(pending.Id)) continue;

            var approval = pending.Kind == PendingKind.Approval;
            var risk = pending.Risk is null ? string.Empty : $"（{pending.Risk.Value.Describe()}）";

            _notifications.Add(
                approval ? NotificationKind.ApprovalPending : NotificationKind.QuestionPending,
                approval ? "需要你的确认" : "需要你的回答",
                FirstLine(pending.Prompt) + risk,
                taskId,
                pending.Id,
                approval
                    ? [new NotificationAction("allow", "允许"), new NotificationAction("deny", "拒绝")]
                    : [],
                NotificationSeverity.Warning,
                "interaction:" + pending.Id);
        }
    }

    private void NotifyTask(string? taskId, NotificationKind kind, NotificationSeverity severity,
        string title, string fallbackBody)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        var snapshot = _center.Task(taskId);
        var name = snapshot?.Title ?? taskId;
        var body = string.IsNullOrWhiteSpace(fallbackBody) ? name : $"{name} · {fallbackBody}";

        _notifications.Add(kind, title, body, taskId, taskId, severity: severity, dedupeKey: $"task:{taskId}:{kind}");
    }

    private static string FirstLine(string value)
    {
        var line = (value ?? string.Empty).Replace("\r", string.Empty).Split('\n').FirstOrDefault() ?? string.Empty;
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _center.Changed -= OnChanged;
    }
}
