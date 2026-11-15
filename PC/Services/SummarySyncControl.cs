namespace MyAiAssistant.Services;

/// <summary>
/// The summary-sync switch and the per-device revoke list.
///
/// Reads and writes go through the same <see cref="PairedDeviceTokens"/> instance the LAN service
/// verifies against, so revoking a device here takes effect on the very next request instead of
/// after a restart.
/// </summary>
public sealed class SummarySyncControl : ISummarySyncControl
{
    private readonly PairedDeviceTokens _tokens;
    private readonly Func<bool> _readEnabled;
    private readonly Action<bool> _persistEnabled;
    private readonly Func<bool>? _readPairControl;
    private readonly Action<bool>? _persistPairControl;
    private readonly Func<bool>? _readNotes;
    private readonly Action<bool>? _persistNotes;

    public SummarySyncControl(PairedDeviceTokens tokens, Func<bool> readEnabled, Action<bool> persistEnabled,
        Func<bool>? readPairControl = null, Action<bool>? persistPairControl = null,
        Func<bool>? readNotes = null, Action<bool>? persistNotes = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _readEnabled = readEnabled ?? throw new ArgumentNullException(nameof(readEnabled));
        _persistEnabled = persistEnabled ?? throw new ArgumentNullException(nameof(persistEnabled));
        _readPairControl = readPairControl;
        _persistPairControl = persistPairControl;
        _readNotes = readNotes;
        _persistNotes = persistNotes;
    }

    public SummarySyncView Read()
    {
        var devices = _tokens.Devices.Select(name => new PairedDeviceView(name)).ToArray();
        return new SummarySyncView(
            _readEnabled(),
            _readPairControl?.Invoke() ?? false,
            _readNotes?.Invoke() ?? false,
            devices.Length,
            devices,
            _tokens.LastError);
    }

    public SummarySyncActionResult Apply(bool enabled)
    {
        _persistEnabled(enabled);

        return enabled
            ? new SummarySyncActionResult(true,
                _tokens.Devices.Count == 0
                    ? "已允许同步摘要，但还没有配对过的手机；先配对，手机端才拿得到令牌"
                    : $"已允许 {_tokens.Devices.Count} 台已配对设备读取任务摘要")
            : new SummarySyncActionResult(true, "已关闭摘要同步，已配对设备将收到 403");
    }

    public SummarySyncActionResult ApplyPairControl(bool enabled)
    {
        if (_persistPairControl is null)
            return new SummarySyncActionResult(false, "这个版本没有接远程控制开关");

        _persistPairControl(enabled);

        return enabled
            ? new SummarySyncActionResult(true,
                _tokens.Devices.Count == 0
                    ? "已允许手机查看任务与待审批，但还没有配对过的手机"
                    : $"已允许 {_tokens.Devices.Count} 台已配对手机查看任务并回答待审批")
            : new SummarySyncActionResult(true, "已关闭：手机不再能看到任务，也不能替你回答");
    }

    public SummarySyncActionResult ApplyNotes(bool enabled)
    {
        if (_persistNotes is null)
            return new SummarySyncActionResult(false, "这个版本没有接笔记同步开关");

        _persistNotes(enabled);

        return enabled
            ? new SummarySyncActionResult(true,
                _tokens.Devices.Count == 0
                    ? "已允许笔记同步，但还没有配对过的手机；先配对，手机端才拿得到令牌"
                    : $"已允许 {_tokens.Devices.Count} 台已配对设备的笔记与这台电脑互相同步")
            : new SummarySyncActionResult(true, "已关闭笔记同步，已配对设备的同步请求将收到 403");
    }

    public SummarySyncActionResult Revoke(string deviceName)    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return new SummarySyncActionResult(false, "请指定设备");

        return _tokens.Revoke(deviceName)
            ? new SummarySyncActionResult(true, $"已撤销 {deviceName} 的令牌，它需要重新配对")
            : new SummarySyncActionResult(false, $"没有找到设备 {deviceName} 的令牌");
    }
}
