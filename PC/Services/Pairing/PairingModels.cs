namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// What a paired device may ask this machine to do.
///
/// Pairing on its own grants none of these: a device that holds the group key can talk, but the
/// capability has to be granted member by member and can be withdrawn at any time. The three
/// high-risk ones are still not enough on their own — a granted capability only means the request
/// is allowed to reach the approval gate, where the user sees and confirms each single call.
/// </summary>
public enum PairingCapability
{
    ReadStatus = 0,
    Chat,
    FileTransfer,
    ConfigSync,
    CommandRelay,
    ScreenControl,
    ShellExec
}

public static class PairingCapabilityText
{
    public static string Describe(this PairingCapability capability) => capability switch
    {
        PairingCapability.ReadStatus => "查看状态",
        PairingCapability.Chat => "代为提问",
        PairingCapability.FileTransfer => "文件互传",
        PairingCapability.ConfigSync => "配置同步",
        PairingCapability.CommandRelay => "命令中转",
        PairingCapability.ScreenControl => "屏幕操作",
        PairingCapability.ShellExec => "受控命令",
        _ => capability.ToString()
    };

    /// <summary>
    /// Capabilities that change this machine or move its data out. Granting one still requires the
    /// user to confirm every individual call; nothing here can run unattended.
    /// </summary>
    public static bool RequiresPerCallApproval(this PairingCapability capability) =>
        capability is PairingCapability.CommandRelay
            or PairingCapability.ScreenControl
            or PairingCapability.ShellExec;
}

/// <summary>One member of a device group, as this machine has it on record.</summary>
public sealed record GroupMember(
    string DeviceId,
    string DisplayName,
    string SigningPublicKey,
    string AgreementPublicKey,
    DateTimeOffset JoinedAt,
    IReadOnlyList<PairingCapability> Capabilities)
{
    /// <summary>Grouped form of <see cref="DeviceId"/>, for showing on screen.</summary>
    public string ShortId => Base32.Group(DeviceId);

    public bool Allows(PairingCapability capability) => Capabilities.Contains(capability);
}

/// <summary>
/// A device group. Members, keys, commands, files and configuration spaces are separate per
/// group, and the group key is never part of this record — it stays wrapped in the store.
/// </summary>
public sealed record DeviceGroup(
    string GroupId,
    string Name,
    DateTimeOffset CreatedAt,
    int KeyEpoch,
    string KeyFingerprint,
    IReadOnlyList<GroupMember> Members)
{
    public string ShortId => Base32.Group(GroupId);
}

/// <summary>
/// What the joining side sends first. Everything that identifies the joiner is a public key, not
/// a name: a device name can be set to anything by anyone, so it is only ever a label.
/// </summary>
public sealed record PairingRequest(
    string InviteId,
    string GroupId,
    string DeviceId,
    string DisplayName,
    string SigningPublicKey,
    string AgreementPublicKey,
    string EphemeralPublicKey,
    string Nonce);

/// <summary>What the inviting side answers with. The group key is wrapped, never sent in the clear.</summary>
public sealed record PairingResponse(
    string GroupId,
    string HostDeviceId,
    string HostDisplayName,
    string HostSigningPublicKey,
    string HostAgreementPublicKey,
    string EphemeralPublicKey,
    string WrappedGroupKey,
    string KeyFingerprint,
    string Signature);

/// <summary>
/// Shown once when an invite is created. The code is the only secret that authenticates the
/// exchange, so it is deliberately kept out of every list and log.
/// </summary>
public sealed record PairingInvite(
    string InviteId,
    string GroupId,
    string Code,
    DateTimeOffset ExpiresAt,
    int MaxAttempts);

/// <summary>An invite as listed for the user: no code, and how many tries are left.</summary>
public sealed record PairingInviteInfo(
    string InviteId,
    string GroupId,
    DateTimeOffset ExpiresAt,
    int RemainingAttempts);

public static class PairingErrors
{
    public const string UnknownInvite = "unknown-invite";
    public const string InviteExpired = "invite-expired";
    public const string InviteConsumed = "invite-consumed";
    public const string TooManyAttempts = "too-many-attempts";
    public const string CodeMismatch = "code-mismatch";
    public const string GroupMismatch = "group-mismatch";
    public const string SelfPairing = "self-pairing";
    public const string BadSignature = "bad-signature";
    public const string BadWrap = "bad-wrap";
    public const string MalformedRequest = "malformed-request";
    public const string UnknownGroup = "unknown-group";
    public const string UnknownMember = "unknown-member";
    public const string DuplicateMember = "duplicate-member";
}

public sealed record PairingOutcome(bool Ok, string Message, string Error = "", string GroupId = "",
    string KeyFingerprint = "", GroupMember? Member = null, PairingInvite? Invite = null)
{
    public static PairingOutcome Refuse(string error, string message) => new(false, message, error);
}

/// <summary>
/// Wraps secrets at rest. The real implementation delegates to <c>ApiKeyProtector</c>; tests pass a
/// reversible stand-in so they never touch the machine's key files.
/// </summary>
public interface IPairingSecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
