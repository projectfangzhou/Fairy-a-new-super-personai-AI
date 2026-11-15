using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services;

/// <summary>
/// Turns an approval request into a real question: a pending item in the shared task list that
/// the Dashboard, the HUD and the Fairy Code window all display. Answering it there releases the
/// caller. Cancellation and timeout are reported back as a refusal, never as consent.
/// </summary>
public sealed class PendingQueueApprover : IHomeAssistantApprover
{
    private readonly AgentControlCenter _center;

    public PendingQueueApprover(AgentControlCenter center) =>
        _center = center ?? throw new ArgumentNullException(nameof(center));

    public async Task<bool> AskAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var task = _center.Enqueue(request.Summary);
        task.Start();

        var prompt = request.Target is null
            ? request.Summary
            : $"{request.Summary}\n目标：{request.Target}";

        try
        {
            var approved = await task.AskApprovalAsync(prompt, request.Category, cancellationToken).ConfigureAwait(false);
            if (approved) task.Complete();
            else task.Fail("用户拒绝");
            return approved;
        }
        catch (OperationCanceledException)
        {
            task.Fail("审批超时或已取消");
            return false;
        }
    }
}
