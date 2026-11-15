using System.Diagnostics;

namespace MyAiAssistant.Services.Permissions;

public sealed record ApprovalResult(bool IsAllowed, string Decision, string Reason)
{
    public static ApprovalResult Allowed(string reason) => new(true, "allowed", reason);
    public static ApprovalResult Denied(string decision, string reason) => new(false, decision, reason);
}

/// <summary>
/// The single door every risky action has to pass. It fails closed: with no approver
/// registered, on timeout, or on an approver error, the action is denied rather than
/// permitted by default.
/// </summary>
public sealed class ApprovalGate
{
    private readonly PermissionPolicy _policy;
    private readonly OperationAudit _audit;

    public ApprovalGate(PermissionPolicy policy, OperationAudit audit)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Set by the UI layer to actually ask the user. While it is null every risky action
    /// is denied, because there is nothing that can legitimately grant consent.
    /// </summary>
    public Func<ActionRequest, CancellationToken, Task<bool>>? Approver { get; set; }

    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Authorizes one action. <paramref name="approver"/> lets a caller supply a task-scoped
    /// approver (for example one bound to a single agent task) instead of the global
    /// <see cref="Approver"/>. It cannot weaken anything: a null approver still means
    /// "deny", and the policy is always evaluated first.
    /// </summary>
    public async Task<ApprovalResult> AuthorizeAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var decision = _policy.Evaluate(request);

        ApprovalResult result;
        string? reviewer = null;

        switch (decision.Verdict)
        {
            case PermissionVerdict.Deny:
                result = ApprovalResult.Denied("denied-by-policy", decision.Reason);
                break;

            case PermissionVerdict.Allow:
                result = ApprovalResult.Allowed(decision.Reason);
                break;

            default:
                (result, reviewer) = await AskUserAsync(request, approver ?? Approver, cancellationToken).ConfigureAwait(false);
                break;
        }

        stopwatch.Stop();
        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow, request.ActionId, request.Category, request.Summary,
            request.Target, result.Decision, result.Reason, reviewer, stopwatch.ElapsedMilliseconds,
            request.Parameters?.Keys.ToArray()));

        return result;
    }

    private async Task<(ApprovalResult Result, string? Reviewer)> AskUserAsync(
        ActionRequest request,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver,
        CancellationToken cancellationToken)
    {
        if (approver is null)
            return (ApprovalResult.Denied("no-approver", "没有可用的审批界面，按失败关闭拒绝"), null);

        using var timeout = new CancellationTokenSource(ApprovalTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var approved = await approver(request, linked.Token).ConfigureAwait(false);
            return approved
                ? (ApprovalResult.Allowed("用户已批准"), "user")
                : (ApprovalResult.Denied("user-denied", "用户拒绝"), "user");
        }
        catch (OperationCanceledException)
        {
            // Order matters for an accurate audit reason: the caller cancelling, the
            // deadline firing and the approver withdrawing itself all look alike here.
            if (cancellationToken.IsCancellationRequested)
                return (ApprovalResult.Denied("cancelled", "操作已取消"), "user");
            if (timeout.IsCancellationRequested)
                return (ApprovalResult.Denied("timeout", "审批超时，按失败关闭拒绝"), "user");
            return (ApprovalResult.Denied("approver-withdrew", "审批被撤回，按失败关闭拒绝"), "user");
        }
        catch (Exception ex)
        {
            return (ApprovalResult.Denied("approver-failed", $"审批过程出错：{ex.GetType().Name}"), "user");
        }
    }
}