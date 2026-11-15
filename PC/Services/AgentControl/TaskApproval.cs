using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.AgentControl;

/// <summary>
/// Connects the approval gate to the task state. A risky action asked for by a task shows
/// up as a pending approval, so the Dashboard/HUD/CLI all display the same request the
/// gate is waiting on, and answering it there releases the gate.
/// </summary>
public static class TaskApproval
{
    public static Func<ActionRequest, CancellationToken, Task<bool>> For(AgentTaskHandle task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (request, cancellationToken) =>
        {
            var prompt = request.Target is null
                ? request.Summary
                : $"{request.Summary}\n目标：{request.Target}";
            return task.AskApprovalAsync(prompt, request.Category, cancellationToken);
        };
    }
}