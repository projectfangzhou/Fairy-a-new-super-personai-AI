using System.Security.Cryptography;
using System.Text;
using MyAiAssistant.Services.Pairing;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

// Sandboxed runs cannot always write to the user temp directory, so keep the scratch workspace
// next to the test binary.
var workspace = Path.Combine(AppContext.BaseDirectory, "test-workspace-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

var now = DateTimeOffset.UtcNow;
DateTimeOffset Clock() => now;
var protector = new XorProtector();

DeviceGroupStore NewStore(string name) => new(Path.Combine(workspace, name), protector, Clock);

// ---- 1. Device identity is derived from the key, not chosen ----
{
    using var first = DeviceIdentity.Create();
    using var second = DeviceIdentity.Create();

    Assert(first.DeviceId.StartsWith("DEV", StringComparison.Ordinal), "the device id is prefixed with DEV");
    Assert(first.DeviceId.Length > 10, "the device id is long enough to be a fingerprint");
    Assert(first.DeviceId != second.DeviceId, "two devices get different ids");
    Assert(first.DeviceId.IndexOf('-') < 0, "the stored device id carries no separators");
    Assert(first.ShortId.Contains('-'), "the displayed device id is grouped for reading");

    var keys = first.ExportPrivateKeys();
    using var restored = DeviceIdentity.FromPrivateKeys(keys.Signing, keys.Agreement);
    Assert(restored.DeviceId == first.DeviceId, "a restored identity keeps its device id");
    Assert(restored.SigningPublicKey == first.SigningPublicKey, "a restored identity keeps its signing key");

    var message = Encoding.UTF8.GetBytes("配对请求");
    var signature = first.Sign(message);
    Assert(DeviceIdentity.Verify(first.SigningPublicKey, message, signature), "a signature verifies with the right key");
    Assert(!DeviceIdentity.Verify(second.SigningPublicKey, message, signature), "a signature does not verify with another key");

    var tampered = Encoding.UTF8.GetBytes("配对请求.");
    Assert(!DeviceIdentity.Verify(first.SigningPublicKey, tampered, signature), "a tampered message fails verification");
    Assert(!DeviceIdentity.Verify("not-base64", message, signature), "an unreadable key is a failed verification, not an exception");
}

// ---- 2. Invites: one code, one group, one success, a few tries ----
{
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create("GRPAAAAAAAAAAAAAAAA");

    Assert(invite.Code.Length == 6, "the pairing code is six characters");
    Assert(invite.Code.All(char.IsAsciiDigit), "the pairing code is digits only");
    Assert(invite.MaxAttempts == 5, "five attempts by default");
    Assert(invite.ExpiresAt == now.AddMinutes(10), "an invite lives ten minutes");

    var listed = invites.Active();
    Assert(listed.Count == 1, "the invite is listed while it is usable");
    Assert(!listed[0].ToString()!.Contains(invite.Code, StringComparison.Ordinal),
        "the listed invite never contains the code");

    var wrong = invites.Verify(invite.InviteId, invite.Code == "000000" ? "111111" : "000000");
    Assert(!wrong.Ok && wrong.Error == PairingErrors.CodeMismatch, "a wrong code is refused as a code mismatch");
    Assert(wrong.Message.Contains("还可以再试 4 次", StringComparison.Ordinal), "the refusal counts the remaining tries");

    var right = invites.Verify(invite.InviteId, invite.Code);
    Assert(right.Ok, "the right code is accepted");
    Assert(right.Invite is not null && right.Invite.Code == invite.Code, "the accepted invite is returned to the caller");

    var again = invites.Verify(invite.InviteId, invite.Code);
    Assert(!again.Ok && again.Error == PairingErrors.InviteConsumed, "the same invite cannot be used twice");
    Assert(invites.Active().Count == 0, "a used invite is no longer listed");

    var unknown = invites.Verify("INVDOESNOTEXIST", "123456");
    Assert(!unknown.Ok && unknown.Error == PairingErrors.UnknownInvite, "an unknown invite is refused");

    var cancelled = invites.Create("GRPAAAAAAAAAAAAAAAA");
    Assert(invites.Cancel(cancelled.InviteId), "an invite can be cancelled");
    var afterCancel = invites.Verify(cancelled.InviteId, cancelled.Code);
    Assert(!afterCancel.Ok && afterCancel.Error == PairingErrors.UnknownInvite, "a cancelled invite is refused");
}

// ---- 3. Too many guesses burn the invite, even if the right code comes later ----
{
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create("GRPAAAAAAAAAAAAAAAA", maxAttempts: 3);
    var decoy = invite.Code == "000000" ? "111111" : "000000";

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var result = invites.Verify(invite.InviteId, decoy);
        Assert(!result.Ok, $"guess {attempt} is refused");
    }

    var fourth = invites.Verify(invite.InviteId, invite.Code);
    Assert(!fourth.Ok, "the invite is gone after the last allowed guess");
    Assert(fourth.Error is PairingErrors.TooManyAttempts or PairingErrors.UnknownInvite,
        "the refusal says the invite was burned, not that the code was wrong");
    Assert(invites.Active().Count == 0, "a burned invite is not listed");
}

// ---- 4. Expiry ----
{
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create("GRPAAAAAAAAAAAAAAAA", lifetime: TimeSpan.FromMinutes(1));

    now = now.AddMinutes(2);
    var expired = invites.Verify(invite.InviteId, invite.Code);
    Assert(!expired.Ok && expired.Error == PairingErrors.InviteExpired, "an expired invite is refused even with the right code");
    Assert(invites.Active().Count == 0, "an expired invite is not listed");
    now = now.AddMinutes(-2);
}

// ---- 5. Codes are not the same every time ----
{
    var invites = new PairingInviteStore(Clock);
    var codes = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < 8; index++) codes.Add(invites.Create("GRPAAAAAAAAAAAAAAAA").Code);
    Assert(codes.Count > 1, "a new invite does not reuse the previous code");
}

// ---- 6. A full pairing: both sides end up with the same group key ----
{
    using var store = NewStore("happy");
    var (created, _, group) = store.CreateGroup("我的设备");
    Assert(created && group is not null, "a group is created");

    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create(group!.GroupId);

    using var phone = DeviceIdentity.Create();
    using var joiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");

    var checkedInvite = invites.Verify(invite.InviteId, invite.Code);
    Assert(checkedInvite.Ok, "the code checks out before the answer is built");

    var hostKey = store.TryGetGroupKey(group.GroupId)!;
    var responded = PairingResponder.Respond(joiner.Request, store.Identity, "台式机", invite, hostKey);
    Assert(responded.Ok && responded.Response is not null, "the host produces an answer");

    var joined = joiner.Accept(invite.Code, responded.Response!);
    Assert(joined.Ok, "the joiner accepts the answer with the right code");
    Assert(joined.GroupKey is not null && joined.GroupKey.SequenceEqual(hostKey),
        "both sides end up with the same group key");
    Assert(joined.Status.KeyFingerprint == DeviceIdentity.Fingerprint(hostKey), "the key fingerprint matches on both sides");
    Assert(joined.Host is not null && joined.Host.DeviceId == store.Identity.DeviceId,
        "the joiner records who it paired with, by key not by name");
    Assert(joined.Host!.SigningPublicKey == store.Identity.SigningPublicKey, "the joiner records the host signing key");

    var (added, _, member) = store.AddMember(group.GroupId, joiner.Request);
    Assert(added && member is not null, "the host records the new member");
    Assert(member!.DeviceId == phone.DeviceId, "the member is recorded by device id");
    Assert(member.DisplayName == "手机", "the member keeps its display name as a label");
    Assert(member.Capabilities.Count == 0, "pairing alone grants no capability");
    Assert(!store.Allows(group.GroupId, member.DeviceId, PairingCapability.ReadStatus),
        "a freshly paired device cannot even read status");

    var stored = store.Find(group.GroupId)!;
    Assert(stored.Members.Count == 1, "the group now has one member");
    Assert(stored.KeyEpoch == 1, "the first key is epoch 1");
}

// ---- 7. The signature is not the anchor: the code is ----
{
    using var store = NewStore("mitm");
    var (_, _, group) = store.CreateGroup("我的设备");
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create(group!.GroupId);
    var hostKey = store.TryGetGroupKey(group.GroupId)!;

    using var phone = DeviceIdentity.Create();
    using var joiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");

    // A device in the middle that does not know the code runs its own valid host: the answer it
    // builds is correctly signed with its own key, so verification alone would accept it.
    using var attacker = DeviceIdentity.Create();
    var decoyInvite = new PairingInvite(invite.InviteId, invite.GroupId, "999999", invite.ExpiresAt, invite.MaxAttempts);
    var forged = PairingResponder.Respond(joiner.Request, attacker, "假冒主机", decoyInvite, hostKey);
    Assert(forged.Ok, "the attacker can build a well-formed answer");

    var result = joiner.Accept(invite.Code, forged.Response!);
    Assert(!result.Ok, "the joiner refuses an answer that was wrapped with another code");
    Assert(result.Status.Error == PairingErrors.CodeMismatch,
        "it fails at unwrapping, which is what proves the code is the anchor rather than the signature");
}

// ---- 8. Tampering is caught ----
{
    using var store = NewStore("tamper");
    var (_, _, group) = store.CreateGroup("我的设备");
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create(group!.GroupId);
    var hostKey = store.TryGetGroupKey(group.GroupId)!;

    using var phone = DeviceIdentity.Create();
    using var joiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");
    var honest = PairingResponder.Respond(joiner.Request, store.Identity, "台式机", invite, hostKey).Response!;

    var flipped = FlipFirstByte(honest.WrappedGroupKey);
    var tamperedKey = honest with { WrappedGroupKey = flipped };
    var withTamperedKey = joiner.Accept(invite.Code, tamperedKey);
    Assert(!withTamperedKey.Ok, "a modified wrapped key is refused");

    using var otherJoiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");
    var otherRequest = PairingResponder.Respond(otherJoiner.Request, store.Identity, "台式机", invite, hostKey).Response!;
    var swapped = otherRequest with { HostSigningPublicKey = DeviceIdentity.Create().SigningPublicKey };
    var withSwappedKey = otherJoiner.Accept(invite.Code, swapped);
    Assert(!withSwappedKey.Ok && withSwappedKey.Status.Error == PairingErrors.BadSignature,
        "swapping the host key breaks the signature instead of being silently trusted");

    using var thirdJoiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");
    var thirdResponse = PairingResponder.Respond(thirdJoiner.Request, store.Identity, "台式机", invite, hostKey).Response!;
    var wrongGroup = thirdJoiner.Accept(invite.Code, thirdResponse with { GroupId = "GRPZZZZZZZZZZZZZZ" });
    Assert(!wrongGroup.Ok && wrongGroup.Status.Error == PairingErrors.GroupMismatch,
        "an answer for another group is refused");

    using var fourthJoiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");
    var fourthResponse = PairingResponder.Respond(fourthJoiner.Request, store.Identity, "台式机", invite, hostKey).Response!;
    var secondUse = fourthJoiner.Accept(invite.Code, fourthResponse);
    Assert(secondUse.Ok, "a fresh session works once");
    var third = fourthJoiner.Accept(invite.Code, fourthResponse);
    Assert(!third.Ok && third.Status.Error == PairingErrors.InviteConsumed,
        "the same session cannot be used for a second exchange");
}

// ---- 9. Refusals the host makes before answering ----
{
    using var store = NewStore("refuse");
    var (_, _, group) = store.CreateGroup("我的设备");
    var invites = new PairingInviteStore(Clock);
    var invite = invites.Create(group!.GroupId);
    var hostKey = store.TryGetGroupKey(group.GroupId)!;

    using var phone = DeviceIdentity.Create();
    using var joiner = new PairingJoinerSession(phone, invite.InviteId, group.GroupId, "手机");

    var selfResponse = PairingResponder.Respond(joiner.Request, DeviceIdentity.FromPrivateKeys(
        phone.ExportPrivateKeys().Signing, phone.ExportPrivateKeys().Agreement), "本机", invite, hostKey);
    Assert(!selfResponse.Ok && selfResponse.Status.Error == PairingErrors.SelfPairing,
        "a device cannot pair with itself");

    var otherGroupInvite = new PairingInvite(invite.InviteId, "GRPOTHEROTHEROTHER", invite.Code, invite.ExpiresAt, invite.MaxAttempts);
    var mismatch = PairingResponder.Respond(joiner.Request, store.Identity, "台式机", otherGroupInvite, hostKey);
    Assert(!mismatch.Ok && mismatch.Status.Error == PairingErrors.GroupMismatch,
        "the request has to belong to the invited group");

    var wrongShortKey = PairingResponder.Respond(joiner.Request, store.Identity, "台式机", invite, new byte[16]);
    Assert(!wrongShortKey.Ok, "a group key of the wrong length is refused instead of used");
}

// ---- 10. Grants and withdrawals ----
{
    using var store = NewStore("grants");
    var (_, _, group) = store.CreateGroup("我的设备");
    var member = AddFakeMember(store, group!.GroupId, "平板");

    var grant = store.Grant(group.GroupId, member.DeviceId, PairingCapability.FileTransfer);
    Assert(grant.Ok, "a capability can be granted");
    Assert(store.Allows(group.GroupId, member.DeviceId, PairingCapability.FileTransfer), "the grant takes effect");
    Assert(!store.Allows(group.GroupId, member.DeviceId, PairingCapability.ShellExec), "granting one does not grant another");

    Assert(store.Grant(group.GroupId, member.DeviceId, PairingCapability.FileTransfer).Ok, "granting twice is harmless");
    Assert(store.Grant("GRPNOTAREALGROUP00", member.DeviceId, PairingCapability.Chat).Ok == false, "granting in an unknown group fails");
    Assert(store.Grant(group.GroupId, "DEVUNKNOWN", PairingCapability.Chat).Ok == false, "granting to an unknown device fails");

    var shell = store.Grant(group.GroupId, member.DeviceId, PairingCapability.ShellExec);
    Assert(shell.Ok && shell.Message.Contains("仍需你当场确认", StringComparison.Ordinal),
        "a high-risk capability says every call still needs confirmation");

    var withdraw = store.Withdraw(group.GroupId, member.DeviceId, PairingCapability.FileTransfer);
    Assert(withdraw.Ok && !store.Allows(group.GroupId, member.DeviceId, PairingCapability.FileTransfer),
        "withdrawing a capability takes effect");
    Assert(store.Withdraw(group.GroupId, member.DeviceId, PairingCapability.FileTransfer).Ok,
        "withdrawing something already withdrawn is harmless");

    Assert(PairingCapability.ShellExec.RequiresPerCallApproval(), "shell execution needs per-call approval");
    Assert(PairingCapability.ScreenControl.RequiresPerCallApproval(), "screen control needs per-call approval");
    Assert(PairingCapability.CommandRelay.RequiresPerCallApproval(), "command relay needs per-call approval");
    Assert(!PairingCapability.FileTransfer.RequiresPerCallApproval(), "file transfer does not need per-call approval");
}

// ---- 11. Revocation rotates the key so the old one stops working ----
{
    using var store = NewStore("revoke");
    var (_, _, group) = store.CreateGroup("我的设备");
    var groupId = group!.GroupId;

    using var keptIdentity = DeviceIdentity.Create();
    var kept = AddFakeMember(store, groupId, "平板", keptIdentity.AgreementPublicKey);
    var removed = AddFakeMember(store, groupId, "旧手机");

    var oldKey = store.TryGetGroupKey(groupId)!;
    Assert(store.AcceptsEpoch(groupId, 1), "payloads on the first key are accepted");

    var revoked = store.RemoveMember(groupId, removed.DeviceId);
    Assert(revoked.Ok, "a member can be removed");
    Assert(revoked.KeyEpoch == 2, "removal moves the group to the next key");
    Assert(!store.AcceptsEpoch(groupId, 1), "payloads on the old key are refused");
    Assert(store.AcceptsEpoch(groupId, 2), "payloads on the new key are accepted");

    var newKey = store.TryGetGroupKey(groupId)!;
    Assert(!newKey.SequenceEqual(oldKey), "the group key actually changed");

    var afterRevoke = store.Find(groupId)!;
    Assert(afterRevoke.Members.Count == 1, "only the other member is left");
    Assert(!store.Allows(groupId, removed.DeviceId, PairingCapability.FileTransfer) == true,
        "a removed device has no capabilities");
    Assert(!store.RemoveMember(groupId, removed.DeviceId).Ok, "removing an absent member is refused");

    Assert(revoked.Rekeys.Count == 1, "the remaining member gets the new key sealed for it");
    var accepted = PairingRekey.Accept(keptIdentity, revoked.Rekeys[0], out var receivedKey);
    Assert(accepted.Ok && receivedKey is not null, "the remaining member can unpack the new key");
    Assert(receivedKey!.SequenceEqual(newKey), "the unpacked key is the new group key");
    Assert(accepted.KeyFingerprint == DeviceIdentity.Fingerprint(newKey), "the fingerprint matches after the rotation");

    using var stranger = DeviceIdentity.Create();
    var strangerAttempt = PairingRekey.Accept(stranger, revoked.Rekeys[0], out var strangerKey);
    Assert(!strangerAttempt.Ok && strangerKey is null, "a device the blob was not sealed for cannot open it");
}

// ---- 12. Several groups stay apart ----
{
    using var store = NewStore("groups");
    var (_, _, first) = store.CreateGroup("家用");
    var (_, _, second) = store.CreateGroup("工作");

    Assert(store.Groups.Count == 2, "both groups exist");
    Assert(store.ActiveGroupId == first!.GroupId, "the first group is active by default");
    Assert(first.GroupId != second!.GroupId, "each group gets its own id");
    Assert(!store.TryGetGroupKey(first.GroupId)!.SequenceEqual(store.TryGetGroupKey(second.GroupId)!),
        "each group has its own key");

    var switched = store.SetActiveGroup(second.GroupId);
    Assert(switched.Ok && store.ActiveGroup!.GroupId == second.GroupId, "the active group can be switched");
    Assert(!store.SetActiveGroup("GRPNOTAREALGROUP00").Ok, "switching to an unknown group fails");

    var firstIsolation = store.IsolationFor(first.GroupId);
    var secondIsolation = store.IsolationFor(second.GroupId);
    Assert(firstIsolation.Root != secondIsolation.Root, "the two groups never share a directory");

    var firstFile = firstIsolation.PathFor(GroupSpace.Files, "a.txt");
    var secondFile = secondIsolation.PathFor(GroupSpace.Files, "a.txt");
    Assert(firstFile != secondFile, "the same file name lands in different places per group");
    Assert(firstFile.StartsWith(firstIsolation.Root, StringComparison.OrdinalIgnoreCase),
        "the file stays inside its own group");
    Assert(!firstFile.StartsWith(secondIsolation.Root, StringComparison.OrdinalIgnoreCase),
        "the file does not land in another group");

    Assert(firstIsolation.KeyFor("apiKey") != secondIsolation.KeyFor("apiKey"),
        "the same configuration key is separate per group");

    var escaped = false;
    try
    {
        firstIsolation.PathFor(GroupSpace.Files, "..");
    }
    catch (ArgumentException)
    {
        escaped = true;
    }
    Assert(escaped, "a name that would climb out of the group directory is refused");

    var badName = false;
    try
    {
        firstIsolation.PathFor(GroupSpace.Files, "../other/a.txt");
    }
    catch (ArgumentException)
    {
        badName = true;
    }
    Assert(badName, "a path separator in the name is refused rather than normalised");

    var unknownGroup = false;
    try
    {
        store.IsolationFor("GRPNOTAREALGROUP00");
    }
    catch (InvalidOperationException)
    {
        unknownGroup = true;
    }
    Assert(unknownGroup, "an unknown group has no isolation to hand out");
}

// ---- 13. What is on disk ----
{
    var directory = Path.Combine(workspace, "persist");
    string firstDeviceId;
    string groupId;
    byte[] firstKey;

    using (var store = new DeviceGroupStore(directory, protector, Clock))
    {
        firstDeviceId = store.Identity.DeviceId;
        var (_, _, group) = store.CreateGroup("持久化");
        groupId = group!.GroupId;
        AddFakeMember(store, groupId, "平板");
        store.Grant(groupId, store.Find(groupId)!.Members[0].DeviceId, PairingCapability.Chat);
        firstKey = store.TryGetGroupKey(groupId)!;
    }

    var json = File.ReadAllText(Path.Combine(directory, "pairing.json"), Encoding.UTF8);
    Assert(json.Contains("GRP", StringComparison.Ordinal), "the store file mentions the group");
    Assert(!json.Contains(Convert.ToBase64String(firstKey), StringComparison.Ordinal),
        "the group key is not written in the clear");
    Assert(!json.Contains("signingPrivateKey\":\"M", StringComparison.Ordinal),
        "the signing key is not written in the clear");

    using (var reopened = new DeviceGroupStore(directory, protector, Clock))
    {
        Assert(reopened.Identity.DeviceId == firstDeviceId, "the device keeps its identity across restarts");
        var group = reopened.Find(groupId);
        Assert(group is not null, "the group survives a restart");
        Assert(group!.Members.Count == 1, "the member survives a restart");
        Assert(group.Members[0].Allows(PairingCapability.Chat), "the grant survives a restart");
        Assert(reopened.TryGetGroupKey(groupId)!.SequenceEqual(firstKey), "the group key is unchanged after a restart");
        Assert(group.KeyFingerprint == reopened.GroupKeyFingerprint(groupId), "the fingerprint is stable");
        Assert(group.KeyFingerprint.Contains('-'), "the fingerprint is displayed in groups");
        Assert(reopened.LastLoadError is null, "a readable store reports no error");
    }
}

// ---- 14. A store file we cannot read is set aside, not silently replaced ----
{
    var directory = Path.Combine(workspace, "corrupt");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "pairing.json"), "{ this is not json", Encoding.UTF8);

    using var store = new DeviceGroupStore(directory, protector, Clock);
    Assert(store.LastLoadError is not null, "an unreadable store file is reported");
    Assert(store.Groups.Count == 0, "nothing is invented from an unreadable file");
    Assert(Directory.GetFiles(directory, "pairing.json.unreadable-*").Length == 1,
        "the unreadable file is kept for inspection");
    Assert(store.Identity.DeviceId.StartsWith("DEV", StringComparison.Ordinal), "a usable identity still comes up");
}

Directory.Delete(workspace, recursive: true);

Console.WriteLine($"All {checks} pairing checks passed.");
return 0;

static GroupMember AddFakeMember(DeviceGroupStore store, string groupId, string name, string? agreementPublicKey = null)
{
    using var identity = DeviceIdentity.Create();
    var request = new PairingRequest(
        "INVFAKE", groupId, identity.DeviceId, name,
        identity.SigningPublicKey,
        agreementPublicKey ?? identity.AgreementPublicKey,
        identity.AgreementPublicKey,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

    var (ok, message, member) = store.AddMember(groupId, request);
    if (!ok || member is null) throw new Exception("could not add test member: " + message);
    return member;
}

static string FlipFirstByte(string base64)
{
    var bytes = Convert.FromBase64String(base64);
    bytes[0] ^= 0xFF;
    return Convert.ToBase64String(bytes);
}

/// <summary>
/// Stands in for <c>ApiKeyProtector</c> so tests do not touch the machine's key files. It is
/// reversible on purpose — the point is to prove the store wraps secrets, not to be secure.
/// </summary>
internal sealed class XorProtector : IPairingSecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(Scramble(Encoding.UTF8.GetBytes(plaintext)));

    public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Scramble(Convert.FromBase64String(ciphertext)));

    private static byte[] Scramble(byte[] input)
    {
        var output = new byte[input.Length];
        for (var index = 0; index < input.Length; index++) output[index] = (byte)(input[index] ^ 0x5A);
        return output;
    }
}
