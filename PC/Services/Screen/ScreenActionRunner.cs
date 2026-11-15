using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.Screen;

/// <summary>What one attempted screen action did.</summary>
public sealed record ScreenActionResult(bool Performed, string Message)
{
    public static ScreenActionResult Refused(string reason) => new(false, reason);

    public static ScreenActionResult Done(string message) => new(true, message);
}

/// <summary>
/// The single door every screen action goes through: availability, then pause/stop, then the user's
/// approval, then the action itself — and the session is told when the action finished, which is
/// where a pause requested mid-action takes effect.
///
/// The tools do not touch <see cref="IScreenControlService"/> directly any more; they hand the work
/// to <see cref="RunAsync"/>, so there is no path that skips a gate.
/// </summary>
public sealed class ScreenActionRunner
{
    private readonly IScreenControlService _screen;
    private readonly ScreenSession _session;
    private readonly Func<ApprovalGate> _gate;
    private readonly Func<ActionRequest, CancellationToken, Task<bool>>? _approver;
    private readonly Func<ScreenControlAvailability> _availability;

    public ScreenActionRunner(
        IScreenControlService screen,
        ScreenSession session,
        Func<ApprovalGate> gate,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver = null,
        Func<ScreenControlAvailability>? availability = null)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _approver = approver;
        _availability = availability ?? ScreenControlAvailability.Detect;
    }

    public ScreenSession Session => _session;

    public ScreenControlAvailability Availability => _availability();

    /// <summary>
    /// Runs one atomic screen action. <paramref name="perform"/> is only invoked when the platform,
    /// the session state and the user all agree; it receives the underlying service so the tool code
    /// stays a one-liner.
    /// </summary>
    public async Task<ScreenActionResult> RunAsync(
        string actionName, string summary, string? target,
        Func<IScreenControlService, Task<string>> perform,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(perform);

        var availability = _availability();
        if (!availability.Supported)
            return ScreenActionResult.Refused(ScreenRefusal.Unsupported(availability.Reason ?? "未知原因"));

        var permit = _session.BeginAction();
        if (!permit.Allowed)
            return ScreenActionResult.Refused(permit.Reason ?? ScreenRefusal.Paused);

        var performed = false;
        try
        {
            var request = new ActionRequest(actionName, RiskCategory.OperateScreenAndInput, summary, target);
            var approval = await _gate().AuthorizeAsync(request, cancellationToken, _approver).ConfigureAwait(false);

            if (!approval.IsAllowed)
                return ScreenActionResult.Refused(ScreenRefusal.Denied(approval.Reason));

            var message = await perform(_screen).ConfigureAwait(false);
            performed = true;
            return ScreenActionResult.Done(message);
        }
        catch (OperationCanceledException)
        {
            return ScreenActionResult.Refused("已取消");
        }
        finally
        {
            _session.EndAction(performed);
        }
    }
}
