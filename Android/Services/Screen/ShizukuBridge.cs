namespace FairyAI_Android.Services.Screen;

/// <summary>
/// How far the phone-local screen layer actually got. Reported as fact, never guessed: everything
/// below <see cref="Authorized"/> means "cannot inject input", and each state carries its own next
/// step so the UI never has to invent reassurance.
/// </summary>
public enum ShizukuAvailability
{
    /// <summary>No Shizuku package on this device.</summary>
    NotInstalled,

    /// <summary>
    /// Shizuku is installed but this build has no binding to its official API
    /// (dev.rikka.shizuku:api), so running/authorization state cannot be read at all.
    /// A build-level gap, not a device problem.
    /// </summary>
    BridgeUnbound,

    /// <summary>The API is bound, but the Shizuku service is not running.</summary>
    NotRunning,

    /// <summary>The API is bound and Shizuku is running, but this app has not been granted access.</summary>
    NotAuthorized,

    /// <summary>Bound, running and granted; injection can be attempted.</summary>
    Authorized,
}

public sealed record ShizukuProbeResult(ShizukuAvailability Availability, string? Version, string Detail)
{
    public bool CanInject => Availability == ShizukuAvailability.Authorized;

    public static ShizukuProbeResult NotInstalled() => new(
        ShizukuAvailability.NotInstalled,
        null,
        "没有检测到 Shizuku。手机上的点击/输入/滑动需要它；不安装也能正常聊天，只是这些能力不可用。");

    public static ShizukuProbeResult Unsupported() => new(
        ShizukuAvailability.BridgeUnbound,
        null,
        "本平台没有 Shizuku 通道：手机内部屏幕操作只在 Android 上提供，其他功能不受影响。");
}

/// <summary>One concrete screen interaction. Coordinates and text only — no free-form commands.</summary>
public sealed record ScreenAction(
    ScreenCapability Capability,
    int X = 0,
    int Y = 0,
    int X2 = 0,
    int Y2 = 0,
    string? Text = null,
    string? Key = null,
    bool AsImage = false)
{
    /// <summary>What the approval prompt shows. Never includes typed text: it may be a password.</summary>
    public string Describe() => Capability switch
    {
        ScreenCapability.Tap => $"点击坐标 ({X}, {Y})",
        ScreenCapability.Swipe => $"从 ({X}, {Y}) 滑动到 ({X2}, {Y2})",
        ScreenCapability.TypeText => $"输入 {Text?.Length ?? 0} 个字符",
        ScreenCapability.PressKey => $"按键 {Key ?? "(未指定)"}",
        ScreenCapability.ReadScreen => AsImage
            ? "截取当前屏幕画面（图片会交给视觉模型）"
            : "读取当前屏幕内容",
        _ => Capability.ToString(),
    };
}

public sealed record ScreenExecutionResult(bool Executed, string Message, byte[]? Payload = null)
{
    public static ScreenExecutionResult Ok(string message = "已执行", byte[]? payload = null) => new(true, message, payload);
    public static ScreenExecutionResult Failed(string message) => new(false, message);
}

/// <summary>
/// The seam for the official Shizuku API. A build that binds dev.rikka.shizuku:api implements this;
/// until then every implementation reports why it cannot inject. Nothing else in the app talks to
/// Shizuku, so wiring the real API later touches exactly one class.
/// </summary>
public interface IShizukuBridge
{
    /// <summary>Raised when the Shizuku side answers something (for example a permission decision).</summary>
    event EventHandler? StatusChanged;

    ShizukuProbeResult Probe();

    /// <summary>Opens whatever screen the user needs in order to install/start/authorize Shizuku.</summary>
    bool OpenShizukuApp(out string message);

    Task<ScreenExecutionResult> InjectAsync(ScreenAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the user to approve one screen action. Separate from Shizuku's own grant on purpose: a grant
/// says "this app may use Shizuku", it does not say "yes, click there, now".
/// </summary>
public interface IScreenApproval
{
    Task<bool> AskAsync(string capabilityLabel, string description, CancellationToken cancellationToken = default);
}

/// <summary>Bridge for platforms where the app has no Shizuku path at all.</summary>
public sealed class UnsupportedShizukuBridge : IShizukuBridge
{
    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public ShizukuProbeResult Probe() => ShizukuProbeResult.Unsupported();

    public bool OpenShizukuApp(out string message)
    {
        message = "本平台没有 Shizuku 通道";
        return false;
    }

    public Task<ScreenExecutionResult> InjectAsync(ScreenAction action, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScreenExecutionResult.Failed("本平台没有 Shizuku 通道，未执行任何操作"));
}
