namespace DashboardTests;

/// <summary>A summary-sync control that answers without touching the configuration file or tokens.</summary>
internal sealed class FakeSummarySyncControl : MyAiAssistant.Services.ISummarySyncControl
{
    private bool _enabled;

    private readonly List<MyAiAssistant.Services.PairedDeviceView> _devices =
    [
        new("Pixel 8"),
    ];

    public bool? LastApplied { get; private set; }

    public string? LastRevoked { get; private set; }

    public MyAiAssistant.Services.SummarySyncView Read() => new(
        _enabled,
        PairControlEnabled,
        NotesSyncEnabled,
        _devices.Count,
        _devices.ToArray(),
        null);

    public bool PairControlEnabled { get; private set; }

    public bool NotesSyncEnabled { get; private set; }

    public bool? LastAppliedNotes { get; private set; }

    public MyAiAssistant.Services.SummarySyncActionResult ApplyNotes(bool enabled)
    {
        LastAppliedNotes = enabled;
        NotesSyncEnabled = enabled;
        return new MyAiAssistant.Services.SummarySyncActionResult(true,
            enabled ? "已允许 1 台已配对设备的笔记与这台电脑互相同步" : "已关闭笔记同步");
    }

    public bool? LastAppliedPairControl { get; private set; }

    public MyAiAssistant.Services.SummarySyncActionResult ApplyPairControl(bool enabled)
    {
        LastAppliedPairControl = enabled;
        PairControlEnabled = enabled;
        return new MyAiAssistant.Services.SummarySyncActionResult(true,
            enabled ? "已允许 1 台已配对手机查看任务并回答待审批" : "已关闭");
    }

    public MyAiAssistant.Services.SummarySyncActionResult Apply(bool enabled)
    {
        LastApplied = enabled;
        _enabled = enabled;
        return new MyAiAssistant.Services.SummarySyncActionResult(true,
            enabled ? "已允许 1 台已配对设备读取任务摘要" : "已关闭摘要同步");
    }

    public MyAiAssistant.Services.SummarySyncActionResult Revoke(string deviceName)
    {
        if (deviceName == "Pixel 8")
        {
            LastRevoked = deviceName;
            _devices.Clear();
            return new MyAiAssistant.Services.SummarySyncActionResult(true, $"已撤销 {deviceName} 的令牌");
        }

        return new MyAiAssistant.Services.SummarySyncActionResult(false, $"没有找到设备 {deviceName} 的令牌");
    }
}
