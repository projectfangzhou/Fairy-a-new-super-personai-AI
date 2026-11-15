namespace FairyAI_Android.Services.Screen;

/// <summary>Stable reason codes for a refusal, so UI text and logs do not have to parse prose.</summary>
public static class ScreenRefusal
{
    public const string Disabled = "screen_control_disabled";
    public const string NotWhitelisted = "capability_not_whitelisted";
    public const string TargetBlocked = "target_package_blocked";
    public const string SensitiveInput = "sensitive_input";
    public const string NotInstalled = "shizuku_not_installed";
    public const string BridgeUnbound = "shizuku_bridge_unbound";
    public const string NotRunning = "shizuku_not_running";
    public const string NotAuthorized = "shizuku_not_authorized";
    public const string NotGranted = "capability_not_granted";
    public const string ApprovalMissing = "approval_unavailable";
    public const string ApprovalDenied = "approval_denied";
    public const string ExecutionFailed = "execution_failed";
}

public sealed record ScreenDecision(bool Allowed, string Reason, string Message, byte[]? Payload = null)
{
    public static ScreenDecision Allow(string message = "", byte[]? payload = null) => new(true, "allowed", message, payload);

    public static ScreenDecision Refuse(string reason, string message) => new(false, reason, message);
}

/// <summary>
/// The single gate every phone-local screen action goes through: policy, availability, per-capability
/// grant, then the user's approval for this one action, and only then the bridge.
///
/// Two orderings are deliberate:
/// <list type="bullet">
/// <item>Policy (blocked target, sensitive input) is checked before availability, so nobody is told
/// "install Shizuku first" while the real answer is "never on this app".</item>
/// <item>Availability and grant are checked before asking for approval, so the user is never asked to
/// approve something that cannot run.</item>
/// </list>
/// Everything is injected, so the whole thing is assertable without a phone.
/// </summary>
public sealed class ScreenGate
{
    private readonly IShizukuBridge _bridge;
    private readonly IScreenApproval? _approval;
    private readonly Func<IReadOnlySet<ScreenCapability>> _grants;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _audit;

    public ScreenGate(
        IShizukuBridge? bridge,
        IScreenApproval? approval = null,
        Func<IReadOnlySet<ScreenCapability>>? grants = null,
        Func<bool>? enabled = null,
        Action<string>? audit = null)
    {
        _bridge = bridge ?? new UnsupportedShizukuBridge();
        _approval = approval;
        _grants = grants ?? (() => new HashSet<ScreenCapability>());
        _enabled = enabled ?? (() => true);
        _audit = audit ?? (_ => { });
    }

    public int Attempts { get; private set; }

    public int Executed { get; private set; }

    public int Refused { get; private set; }

    public ScreenDecision? LastRefusal { get; private set; }

    public ShizukuProbeResult Status()
    {
        try
        {
            return _bridge.Probe();
        }
        catch (Exception ex)
        {
            return new ShizukuProbeResult(
                ShizukuAvailability.BridgeUnbound,
                null,
                "读取 Shizuku 状态时出错：" + ex.Message);
        }
    }

    /// <summary>
    /// Everything except the approval and the injection: what would happen right now, and where it
    /// would stop. Side-effect free, so the UI can show it without asking anything.
    /// </summary>
    public ScreenDecision Inspect(ScreenCapability capability, string? targetPackage = null, string? text = null)
    {
        if (!SafeEnabled())
            return ScreenDecision.Refuse(
                ScreenRefusal.Disabled,
                "手机屏幕操作总开关是关的：在「看板 → 设备连接」里打开后再试。");

        if (!ScreenCapabilityCatalog.IsWhitelisted(capability))
            return ScreenDecision.Refuse(
                ScreenRefusal.NotWhitelisted,
                $"「{capability}」不在允许的能力清单内，不会执行。");

        if (ScreenTargetPolicy.IsBlockedPackage(targetPackage))
            return ScreenDecision.Refuse(
                ScreenRefusal.TargetBlocked,
                $"不会操作金融类应用（{targetPackage}）：这类操作不因 Shizuku 已授权而开放。");

        if (capability == ScreenCapability.TypeText && ScreenTargetPolicy.ContainsSensitiveAction(text))
            return ScreenDecision.Refuse(
                ScreenRefusal.SensitiveInput,
                "这段输入包含支付/密码类字样，不在允许范围内，不会输入。");

        var granted = SafeGrants().Contains(capability);
        var status = Status();

        switch (status.Availability)
        {
            case ShizukuAvailability.NotInstalled:
                return ScreenDecision.Refuse(
                    ScreenRefusal.NotInstalled,
                    "没有检测到 Shizuku。安装 Shizuku 并在里面启动服务后回到这里；不装也能正常聊天，只是这些能力不可用。");

            case ShizukuAvailability.BridgeUnbound:
                return ScreenDecision.Refuse(ScreenRefusal.BridgeUnbound, status.Detail);

            case ShizukuAvailability.NotRunning:
                return ScreenDecision.Refuse(
                    ScreenRefusal.NotRunning,
                    "Shizuku 已安装但服务没在运行：打开一次 Shizuku（或用电脑上的 ADB 启动它的服务）。");

            case ShizukuAvailability.NotAuthorized:
                return ScreenDecision.Refuse(
                    ScreenRefusal.NotAuthorized,
                    "Shizuku 在运行，但还没有授权给仙灵：打开 Shizuku 应用，在授权列表里允许仙灵，然后回到这里。");

            case ShizukuAvailability.Authorized:
                break;

            default:
                return ScreenDecision.Refuse(ScreenRefusal.BridgeUnbound, "Shizuku 状态无法判定，按不可用处理。");
        }

        if (!granted)
            return ScreenDecision.Refuse(
                ScreenRefusal.NotGranted,
                $"「{ScreenCapabilityCatalog.Label(capability)}」还没有被允许：在「看板 → 设备连接 → 手机屏幕操作」里逐项打开。");

        return ScreenDecision.Allow();
    }

    /// <summary>Runs one action all the way through, or refuses and says why.</summary>
    public async Task<ScreenDecision> RunAsync(
        ScreenAction action,
        string? targetPackage = null,
        CancellationToken cancellationToken = default)
    {
        Attempts++;

        var decision = Inspect(action.Capability, targetPackage, action.Text);
        if (!decision.Allowed) return Record(decision);

        if (_approval is null)
            return Record(ScreenDecision.Refuse(ScreenRefusal.ApprovalMissing, "没有可用的审批界面，按不执行处理。"));

        bool approved;
        try
        {
            approved = await _approval.AskAsync(
                ScreenCapabilityCatalog.Label(action.Capability),
                action.Describe(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            approved = false;
        }
        catch (Exception ex)
        {
            return Record(ScreenDecision.Refuse(
                ScreenRefusal.ApprovalMissing,
                "审批时出错，按不执行处理：" + ex.Message));
        }

        if (!approved)
            return Record(ScreenDecision.Refuse(ScreenRefusal.ApprovalDenied, "你在审批里拒绝了这次操作。"));

        try
        {
            var result = await _bridge.InjectAsync(action, cancellationToken).ConfigureAwait(false);
            if (!result.Executed)
                return Record(ScreenDecision.Refuse(ScreenRefusal.ExecutionFailed, result.Message));

            Executed++;
            _audit($"{ScreenCapabilityCatalog.Label(action.Capability)} · {action.Describe()} · 已执行");
            return ScreenDecision.Allow(result.Message, result.Payload);
        }
        catch (OperationCanceledException)
        {
            return Record(ScreenDecision.Refuse(ScreenRefusal.ExecutionFailed, "操作被取消，不保证已经发生。"));
        }
        catch (Exception ex)
        {
            return Record(ScreenDecision.Refuse(ScreenRefusal.ExecutionFailed, "执行失败：" + ex.Message));
        }
    }

    private ScreenDecision Record(ScreenDecision refusal)
    {
        if (!refusal.Allowed)
        {
            Refused++;
            LastRefusal = refusal;
            _audit($"{refusal.Reason} · {refusal.Message}");
        }

        return refusal;
    }

    private bool SafeEnabled()
    {
        try
        {
            return _enabled();
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlySet<ScreenCapability> SafeGrants()
    {
        try
        {
            return _grants() ?? new HashSet<ScreenCapability>();
        }
        catch
        {
            return new HashSet<ScreenCapability>();
        }
    }
}
