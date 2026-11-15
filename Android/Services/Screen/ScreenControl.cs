using System.IO;

namespace FairyAI_Android.Services.Screen;

/// <summary>
/// App-facing wrapper around <see cref="ScreenGate"/>: reads the stored switch and per-capability
/// grants, writes the audit trail, and picks the platform bridge. Thin on purpose — all the decisions
/// live in the gate, which needs no Android to be tested.
/// </summary>
public sealed class ScreenControl
{
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    private readonly ScreenGate _gate;
    private readonly IShizukuBridge _bridge;

    public ScreenControl(IShizukuBridge? bridge = null, IScreenApproval? approval = null, Action<string>? audit = null)
    {
        _bridge = bridge ?? CreateDefaultBridge();
        _bridge.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        _gate = new ScreenGate(
            _bridge,
            approval,
            () => Grants,
            () => ConfigManager.Load().ScreenControl.Enabled,
            message => Audit(audit, message));
    }

    /// <summary>Raised when the bridge reports that the Shizuku side changed (for example a grant).</summary>
    public event EventHandler? StatusChanged;

    public ShizukuProbeResult Status => _gate.Status();

    public int Attempts => _gate.Attempts;

    public int Executed => _gate.Executed;

    public int Refused => _gate.Refused;

    public ScreenDecision? LastRefusal => _gate.LastRefusal;

    /// <summary>What is allowed right now, without asking anything or touching the screen.</summary>
    public ScreenDecision Inspect(ScreenCapability capability, string? targetPackage = null, string? text = null) =>
        _gate.Inspect(capability, targetPackage, text);

    /// <summary>Runs one action through the full gate: policy → availability → grant → approval → bridge.</summary>
    public Task<ScreenDecision> RunAsync(
        ScreenAction action,
        string? targetPackage = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(action, targetPackage, cancellationToken);

    /// <summary>
    /// Captures the screen as PNG, through the same gate as any other screen action — it needs the
    /// read-screen grant and a fresh approval, and the prompt says the picture will be handed to a
    /// vision model.
    /// </summary>
    public Task<ScreenDecision> CaptureAsync(CancellationToken cancellationToken = default) =>
        _gate.RunAsync(new ScreenAction(ScreenCapability.ReadScreen, AsImage: true), null, cancellationToken);

    public bool IsEnabled
    {
        get => ConfigManager.Load().ScreenControl.Enabled;
        set
        {
            var config = ConfigManager.Load();
            config.ScreenControl.Enabled = value;
            ConfigManager.Save(config);
        }
    }

    public IReadOnlySet<ScreenCapability> Grants =>
        ScreenCapabilityCatalog.ParseAll(ConfigManager.Load().ScreenControl.GrantedCapabilities);

    public bool IsGranted(ScreenCapability capability) => Grants.Contains(capability);

    /// <summary>Grants or withdraws one capability. Withdrawing takes effect on the next action.</summary>
    public void SetGranted(ScreenCapability capability, bool granted)
    {
        var config = ConfigManager.Load();
        var current = new HashSet<ScreenCapability>(
            ScreenCapabilityCatalog.ParseAll(config.ScreenControl.GrantedCapabilities));

        if (granted) current.Add(capability);
        else current.Remove(capability);

        config.ScreenControl.GrantedCapabilities = current.Select(c => c.ToString()).ToArray();
        ConfigManager.Save(config);
        Audit(null, granted
            ? $"已允许「{ScreenCapabilityCatalog.Label(capability)}」"
            : $"已收回「{ScreenCapabilityCatalog.Label(capability)}」");
    }

    /// <summary>Opens the screen the user needs to install, start or authorize Shizuku.</summary>
    public bool OpenShizukuApp(out string message) => _bridge.OpenShizukuApp(out message);

    private static IShizukuBridge CreateDefaultBridge()
    {
#if ANDROID
        return new AndroidShizukuBridge();
#else
        return new UnsupportedShizukuBridge();
#endif
    }

    private static void Audit(Action<string>? sink, string message)
    {
        if (sink is not null)
        {
            sink(message);
            return;
        }

        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [SCREEN] {message}\n");
        }
        catch
        {
            // Logging must never break an action.
        }
    }
}
