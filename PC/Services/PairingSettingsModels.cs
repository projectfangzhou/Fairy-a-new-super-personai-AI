using MyAiAssistant.Services.Pairing;

namespace MyAiAssistant.Services;

/// <summary>The control-centre view of pairing: who is in, who may do what, and whether the door is open.</summary>
public sealed record PairingView(
    bool Enabled,
    bool Listening,
    int Port,
    string DisplayName,
    string DeviceId,
    string ShortDeviceId,
    string? ActiveGroupId,
    string? ListenError,
    string? LoadError,
    IReadOnlyList<DeviceGroup> Groups,
    IReadOnlyList<PairingInviteInfo> Invites);

/// <summary>
/// A pairing action's result. <see cref="Code"/> is filled only when a code was just created, and
/// it is the one time it can be read — the UI shows it and then has nothing to show again.
/// </summary>
public sealed record PairingActionResult(bool Ok, string Message, string? Code = null);

/// <summary>
/// What the control centre may ask pairing to do. An interface so the dashboard can be tested
/// without opening a listening socket or touching the real configuration file.
/// </summary>
public interface IPairingControl
{
    PairingView Read();

    PairingActionResult Apply(bool? enabled, int? port, string? displayName);

    PairingActionResult CreateGroup(string name);

    PairingActionResult SetActiveGroup(string groupId);

    PairingActionResult CreateInvite(string? groupId);

    PairingActionResult CancelInvite(string inviteId);

    PairingActionResult Grant(string groupId, string deviceId, string capability);

    PairingActionResult Withdraw(string groupId, string deviceId, string capability);

    PairingActionResult RemoveMember(string groupId, string deviceId);

    PairingActionResult RotateKey(string groupId);
}
