using MyAiAssistant.Services.Pairing;

namespace MyAiAssistant.Services;

/// <summary>
/// Wires pairing to the app: the listener follows the saved setting immediately, and every group
/// action the control centre offers goes through the store rather than around it.
/// </summary>
public sealed class PairingControl : IPairingControl, IDisposable
{
    private readonly PairingService _pairing;
    private readonly PairingEndpointServer _endpoint;

    public PairingControl(PairingService pairing, PairingEndpointServer endpoint)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _pairing.HostDisplayName = ResolveDisplayName(ConfigManager.Load().Pairing);
    }

    /// <summary>Opens the listener at startup when the user had already turned pairing on.</summary>
    public (bool Started, string Message) StartIfEnabled()
    {
        var config = ConfigManager.Load().Pairing;
        if (!config.Enabled)
        {
            _endpoint.Stop();
            return (false, "配对未开启");
        }

        if (_endpoint.IsRunning) return (true, $"配对端口已在监听 {_endpoint.Port}");

        return _endpoint.Start()
            ? (true, $"配对端口已监听 {_endpoint.Port}")
            : (false, $"配对端口未能启动：{_endpoint.LastError ?? "端口被占用或权限不足"}");
    }

    public PairingView Read()
    {
        var config = ConfigManager.Load().Pairing;
        var state = _pairing.Describe();

        return new PairingView(
            config.Enabled,
            _endpoint.IsRunning,
            _endpoint.Port,
            state.DisplayName,
            state.DeviceId,
            state.ShortDeviceId,
            state.ActiveGroupId,
            _endpoint.LastError,
            state.LoadError,
            state.Groups,
            state.Invites);
    }

    public PairingActionResult Apply(bool? enabled, int? port, string? displayName)
    {
        var config = ConfigManager.Load();

        if (port is not null)
        {
            if (port is < 1024 or > 65535) return new PairingActionResult(false, "端口需在 1024 到 65535 之间");
            config.Pairing.Port = port.Value;
        }

        if (displayName is not null)
        {
            var trimmed = displayName.Trim();
            if (trimmed.Length > 40) return new PairingActionResult(false, "设备名最长 40 个字符");
            config.Pairing.DisplayName = trimmed;
        }

        if (enabled is not null) config.Pairing.Enabled = enabled.Value;

        ConfigManager.Save(config);
        _pairing.HostDisplayName = ResolveDisplayName(config.Pairing);

        var portChanged = port is not null;
        if (portChanged) _endpoint.Reconfigure(config.Pairing.Port);

        if (config.Pairing.Enabled)
        {
            if (!_endpoint.IsRunning && !_endpoint.Start())
                return new PairingActionResult(false, $"已保存设置，但配对端口未能启动：{_endpoint.LastError ?? "端口被占用或权限不足"}");

            return new PairingActionResult(true, $"配对已开启，其他设备可以连到 {_endpoint.Port} 端口配对");
        }

        if (_endpoint.IsRunning) _endpoint.Stop();
        return new PairingActionResult(true, "配对已关闭，其他设备无法连入");
    }

    public PairingActionResult CreateGroup(string name)
    {
        var (ok, message) = _pairing.CreateGroup(name);
        return new PairingActionResult(ok, message);
    }

    public PairingActionResult SetActiveGroup(string groupId)
    {
        var (ok, message) = _pairing.SetActiveGroup(groupId);
        return new PairingActionResult(ok, message);
    }

    public PairingActionResult CreateInvite(string? groupId)
    {
        var (ok, message, invite) = _pairing.CreateInvite(groupId);
        return ok && invite is not null
            ? new PairingActionResult(true, message, invite.Code)
            : new PairingActionResult(false, message);
    }

    public PairingActionResult CancelInvite(string inviteId) =>
        _pairing.CancelInvite(inviteId)
            ? new PairingActionResult(true, "邀请已作废")
            : new PairingActionResult(false, "邀请不存在或已失效");

    public PairingActionResult Grant(string groupId, string deviceId, string capability)
    {
        if (!TryParseCapability(capability, out var parsed)) return UnknownCapability(capability);
        var (ok, message) = _pairing.Grant(groupId, deviceId, parsed);
        return new PairingActionResult(ok, message);
    }

    public PairingActionResult Withdraw(string groupId, string deviceId, string capability)
    {
        if (!TryParseCapability(capability, out var parsed)) return UnknownCapability(capability);
        var (ok, message) = _pairing.Withdraw(groupId, deviceId, parsed);
        return new PairingActionResult(ok, message);
    }

    public PairingActionResult RemoveMember(string groupId, string deviceId)
    {
        var (ok, message, _, _) = _pairing.RemoveMember(groupId, deviceId);
        return new PairingActionResult(ok, message);
    }

    public PairingActionResult RotateKey(string groupId)
    {
        var (ok, message, _, _) = _pairing.RotateGroupKey(groupId);
        return new PairingActionResult(ok, message);
    }

    private static string ResolveDisplayName(PairingConfig config) =>
        string.IsNullOrWhiteSpace(config.DisplayName) ? Environment.MachineName : config.DisplayName.Trim();

    private static bool TryParseCapability(string value, out PairingCapability capability)
    {
        capability = default;
        return !string.IsNullOrWhiteSpace(value) &&
               Enum.TryParse(value, ignoreCase: true, out capability) &&
               Enum.IsDefined(capability);
    }

    private static PairingActionResult UnknownCapability(string value) =>
        new(false, $"未知的能力：{value}");

    public void Dispose() => _pairing.Dispose();
}
