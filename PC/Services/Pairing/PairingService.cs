using System.IO;
using System.Security.Cryptography;

namespace MyAiAssistant.Services.Pairing;

/// <summary>Everything the control surface shows about pairing, in one read.</summary>
public sealed record PairingState(
    string DeviceId,
    string ShortDeviceId,
    string DisplayName,
    string? ActiveGroupId,
    IReadOnlyList<DeviceGroup> Groups,
    IReadOnlyList<PairingInviteInfo> Invites,
    string? LoadError);

/// <summary>
/// The app-level pairing operations: what the user (through the control centre) and a joining
/// device (through the network endpoint) are allowed to do, in one place.
///
/// The store keeps the state and the protocol keeps the crypto; this decides the order — verify
/// the invite, record the member, then answer — so a device is never told it joined a group that
/// does not list it, and never added without having proved it knows the code.
/// </summary>
public sealed class PairingService : IDisposable
{
    private readonly DeviceGroupStore _store;
    private readonly PairingInviteStore _invites;

    public PairingService(DeviceGroupStore store, PairingInviteStore invites)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _invites = invites ?? throw new ArgumentNullException(nameof(invites));
    }

    /// <summary>The label the other device sees for this machine.</summary>
    public string HostDisplayName { get; set; } = Environment.MachineName;

    public DeviceGroupStore Store => _store;

    /// <summary>Where pairing state lives when the app has not been given a directory.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "pairing");

    public PairingState Describe()
    {
        var identity = _store.Identity;
        return new PairingState(
            identity.DeviceId,
            identity.ShortId,
            HostDisplayName,
            _store.ActiveGroupId,
            _store.Groups,
            _invites.Active(),
            _store.LastLoadError);
    }

    /// <summary>
    /// Creates an invite for one group. The returned invite carries the code and is the only place
    /// the code is ever visible: it is not stored, not listed and not logged.
    /// </summary>
    public (bool Ok, string Message, PairingInvite? Invite) CreateInvite(string? groupId = null)
    {
        var target = string.IsNullOrWhiteSpace(groupId) ? _store.ActiveGroupId : groupId;
        if (string.IsNullOrWhiteSpace(target))
            return (false, "还没有设备组，请先创建一个", null);

        var group = _store.Find(target);
        if (group is null) return (false, "设备组不存在", null);

        var invite = _invites.Create(group.GroupId);

        // The countdown here is about the bridge, not the secret: once an invite exists, some
        // device has to reach this port to use it.
        var minutes = Math.Max(1, (int)Math.Round((invite.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes));
        return (true, $"已在「{group.Name}」上生成配对码，{minutes} 分钟内有效", invite);
    }

    public bool CancelInvite(string inviteId) => _invites.Cancel(inviteId);

    /// <summary>
    /// Handles a join that arrived from another device. Nothing here trusts the caller beyond the
    /// code: the member is only recorded after the invite accepts it, and the answer is only
    /// produced after the member is recorded.
    /// </summary>
    public PairingRespondResult HandleJoin(string code, PairingRequest request)
    {
        if (request is null) return Refuse(PairingErrors.MalformedRequest, "配对请求为空");

        var checkedInvite = _invites.Verify(request.InviteId, code);
        if (!checkedInvite.Ok) return Refuse(checkedInvite.Error, checkedInvite.Message);

        var invite = checkedInvite.Invite!;
        var groupKey = _store.TryGetGroupKey(invite.GroupId);
        if (groupKey is null) return Refuse(PairingErrors.UnknownGroup, "邀请对应的设备组已不存在");

        var (added, addedMessage, member) = _store.AddMember(invite.GroupId, request);
        if (!added || member is null)
        {
            CryptographicOperations.ZeroMemory(groupKey);
            return Refuse(PairingErrors.DuplicateMember, addedMessage);
        }

        PairingRespondResult answered;
        try
        {
            answered = PairingResponder.Respond(request, _store.Identity, HostDisplayName, invite, groupKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(groupKey);
        }

        if (!answered.Ok)
        {
            // The member was recorded a moment ago; if the answer cannot be produced, undo it so
            // the group never lists a device that never received the key.
            _store.RemoveMember(invite.GroupId, request.DeviceId);
            return answered;
        }

        return answered;
    }

    public (bool Ok, string Message) CreateGroup(string name)
    {
        var (ok, message, _) = _store.CreateGroup(name);
        return (ok, message);
    }

    public (bool Ok, string Message) SetActiveGroup(string groupId) => _store.SetActiveGroup(groupId);

    public (bool Ok, string Message) Grant(string groupId, string deviceId, PairingCapability capability) =>
        _store.Grant(groupId, deviceId, capability);

    public (bool Ok, string Message) Withdraw(string groupId, string deviceId, PairingCapability capability) =>
        _store.Withdraw(groupId, deviceId, capability);

    public (bool Ok, string Message, int KeyEpoch, IReadOnlyList<GroupRekeyBlob> Rekeys) RemoveMember(
        string groupId, string deviceId) => _store.RemoveMember(groupId, deviceId);

    public (bool Ok, string Message, int KeyEpoch, IReadOnlyList<GroupRekeyBlob> Rekeys) RotateGroupKey(string groupId) =>
        _store.RotateGroupKey(groupId);

    private static PairingRespondResult Refuse(string error, string message) =>
        new(PairingOutcome.Refuse(error, message));

    public void Dispose() => _store.Dispose();
}
