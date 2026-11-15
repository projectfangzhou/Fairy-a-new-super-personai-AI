using System.Security.Cryptography;
using System.Text;

namespace MyAiAssistant.Services.Pairing;

public sealed record PairingJoinResult(PairingOutcome Status, byte[]? GroupKey = null, GroupMember? Host = null)
{
    public bool Ok => Status.Ok;
}

public sealed record PairingRespondResult(PairingOutcome Status, PairingResponse? Response = null)
{
    public bool Ok => Status.Ok;
}

/// <summary>
/// The pairing exchange.
///
/// The six-digit code is the only thing that authenticates it. Both sides fold the code into the
/// key that wraps the group key, so a device in the middle that swaps out the ephemeral keys can
/// still not derive that key: it does not know the code. That also means a wrong code fails at
/// unwrapping rather than at a comparable string, and holding the code IS the authority to join —
/// which is why an invite is single-use and expires.
///
/// Long-term keys play no part in deriving the wrap key. They sign the exchange so the joining
/// side can pin who it paired with, and the group key stays protected by the ephemeral shared
/// secret even if a long-term key leaks later.
/// </summary>
public static class PairingProtocol
{
    private const string Label = "仙灵配对 v1";
    private const int GroupKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    internal static byte[] NewGroupKey() => RandomNumberGenerator.GetBytes(GroupKeyLength);

    /// <summary>Canonical bytes both sides must build identically, or nothing unwraps.</summary>
    internal static byte[] ContextDigest(PairingRequest request, PairingResponse response) =>
        ContextDigest(
            request.InviteId, request.GroupId,
            request.DeviceId, request.DisplayName, request.SigningPublicKey, request.AgreementPublicKey,
            request.EphemeralPublicKey, request.Nonce,
            response.HostDeviceId, response.HostDisplayName, response.HostSigningPublicKey,
            response.HostAgreementPublicKey, response.EphemeralPublicKey);

    internal static byte[] ContextDigest(
        string inviteId, string groupId,
        string joinerDeviceId, string joinerName, string joinerSigningKey, string joinerAgreementKey,
        string joinerEphemeralKey, string joinerNonce,
        string hostDeviceId, string hostName, string hostSigningKey,
        string hostAgreementKey, string hostEphemeralKey)
    {
        var builder = new StringBuilder();
        void Add(string value) => builder.Append(value).Append('\n');

        Add(Label);
        Add(inviteId);
        Add(groupId);
        Add(joinerDeviceId);
        Add(joinerName);
        Add(joinerSigningKey);
        Add(joinerAgreementKey);
        Add(joinerEphemeralKey);
        Add(joinerNonce);
        Add(hostDeviceId);
        Add(hostName);
        Add(hostSigningKey);
        Add(hostAgreementKey);
        Add(hostEphemeralKey);

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>What the host signs: the whole exchange including the wrapped key it produced.</summary>
    internal static byte[] SignedDigest(byte[] contextDigest, string wrappedKey, string keyFingerprint)
    {
        var material = Encoding.UTF8.GetBytes(wrappedKey + "\n" + keyFingerprint);
        var buffer = new byte[contextDigest.Length + material.Length];
        contextDigest.CopyTo(buffer, 0);
        material.CopyTo(buffer, contextDigest.Length);
        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// Derives the wrapping key from the ephemeral shared secret, salted with the invite and
    /// finished with the pairing code.
    /// </summary>
    internal static byte[] DeriveWrapKey(ECDiffieHellman ephemeral, string counterpartEphemeralKey,
        byte[] contextDigest, string inviteId, string groupId, string code)
    {
        using var counterpart = DeviceIdentity.ImportAgreementKey(counterpartEphemeralKey);

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(Label + "\n" + inviteId + "\n" + groupId + "\nwrap"));
        var codeBytes = Encoding.UTF8.GetBytes(code);

        return ephemeral.DeriveKeyFromHmac(counterpart.PublicKey, HashAlgorithmName.SHA256, salt,
            contextDigest, codeBytes);
    }

    internal static string Wrap(byte[] wrapKey, byte[] contextDigest, ReadOnlySpan<byte> groupKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[groupKey.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(wrapKey, TagLength))
        {
            aes.Encrypt(nonce, groupKey, ciphertext, tag, contextDigest);
        }

        var blob = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceLength);
        ciphertext.CopyTo(blob, NonceLength + TagLength);
        return Convert.ToBase64String(blob);
    }

    internal static bool TryUnwrap(byte[] wrapKey, byte[] contextDigest, string wrappedKey, out byte[] groupKey)
    {
        groupKey = Array.Empty<byte>();

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(wrappedKey);
        }
        catch (FormatException)
        {
            return false;
        }

        if (blob.Length != NonceLength + TagLength + GroupKeyLength) return false;

        var plaintext = new byte[GroupKeyLength];
        try
        {
            using var aes = new AesGcm(wrapKey, TagLength);
            aes.Decrypt(blob.AsSpan(0, NonceLength), blob.AsSpan(NonceLength + TagLength), blob.AsSpan(NonceLength, TagLength),
                plaintext, contextDigest);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }

        groupKey = plaintext;
        return true;
    }
}

/// <summary>
/// The joining side, in two steps: it sends a request, then answers with the code the user was told.
/// Single use — a session holds its own ephemeral key and must not be reused for a second attempt,
/// because reuse would let one code cover two exchanges.
/// </summary>
public sealed class PairingJoinerSession : IDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly ECDiffieHellman _ephemeral;
    private bool _completed;

    public PairingJoinerSession(DeviceIdentity identity, string inviteId, string groupId, string displayName)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(inviteId)) throw new ArgumentException("邀请编号不能为空", nameof(inviteId));
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("设备组不能为空", nameof(groupId));

        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0) name = "未命名设备";

        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        Request = new PairingRequest(
            inviteId,
            groupId,
            identity.DeviceId,
            name,
            identity.SigningPublicKey,
            identity.AgreementPublicKey,
            Convert.ToBase64String(_ephemeral.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
    }

    public PairingRequest Request { get; }

    public PairingJoinResult Accept(string code, PairingResponse response)
    {
        if (_completed) return Fail(PairingErrors.InviteConsumed, "这次配对请求已经用过了，请重新开始");
        if (response is null) return Fail(PairingErrors.BadWrap, "对方没有返回配对结果");

        _completed = true;

        if (!string.Equals(response.GroupId, Request.GroupId, StringComparison.Ordinal))
            return Fail(PairingErrors.GroupMismatch, "对方返回的设备组与邀请不一致");

        if (string.Equals(response.HostDeviceId, _identity.DeviceId, StringComparison.Ordinal))
            return Fail(PairingErrors.SelfPairing, "不能和自己配对");

        var contextDigest = PairingProtocol.ContextDigest(Request, response);

        var signedDigest = PairingProtocol.SignedDigest(contextDigest, response.WrappedGroupKey, response.KeyFingerprint);
        if (!DeviceIdentity.Verify(response.HostSigningPublicKey, signedDigest, FromBase64(response.Signature)))
            return Fail(PairingErrors.BadSignature, "对方身份签名验证失败");

        using var wrapKeyOwner = new WrapKeyHandle(PairingProtocol.DeriveWrapKey(_ephemeral, response.EphemeralPublicKey,
            contextDigest, Request.InviteId, Request.GroupId, code ?? string.Empty));

        if (!PairingProtocol.TryUnwrap(wrapKeyOwner.Value, contextDigest, response.WrappedGroupKey, out var groupKey))
            return Fail(PairingErrors.CodeMismatch, "配对码不正确，或对方并未用同一码生成密钥");

        if (!string.Equals(DeviceIdentity.Fingerprint(groupKey), response.KeyFingerprint, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(groupKey);
            return Fail(PairingErrors.BadWrap, "组密钥指纹与对方声明的不一致");
        }

        var host = new GroupMember(
            response.HostDeviceId,
            response.HostDisplayName,
            response.HostSigningPublicKey,
            response.HostAgreementPublicKey,
            DateTimeOffset.UtcNow,
            Array.Empty<PairingCapability>());

        return new PairingJoinResult(new PairingOutcome(true, "配对完成", GroupId: response.GroupId,
            KeyFingerprint: response.KeyFingerprint, Member: host), groupKey, host);
    }

    private static PairingJoinResult Fail(string error, string message) =>
        new(PairingOutcome.Refuse(error, message));

    private static byte[] FromBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value ?? string.Empty);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private sealed class WrapKeyHandle : IDisposable
    {
        public WrapKeyHandle(byte[] value) => Value = value;

        public byte[] Value { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(Value);
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
    }
}

/// <summary>The inviting side: turns a request plus the invite into the answer.</summary>
public static class PairingResponder
{
    public static PairingRespondResult Respond(PairingRequest request, DeviceIdentity hostIdentity,
        string hostDisplayName, PairingInvite invite, byte[] groupKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(groupKey);

        if (groupKey.Length != 32)
            return Refuse(PairingErrors.BadWrap, "组密钥长度不正确");

        if (!string.Equals(request.InviteId, invite.InviteId, StringComparison.Ordinal))
            return Refuse(PairingErrors.UnknownInvite, "请求对应的邀请不存在");

        if (!string.Equals(request.GroupId, invite.GroupId, StringComparison.Ordinal))
            return Refuse(PairingErrors.GroupMismatch, "请求的设备组与邀请不一致");

        if (string.Equals(request.DeviceId, hostIdentity.DeviceId, StringComparison.Ordinal))
            return Refuse(PairingErrors.SelfPairing, "不能和自己配对");

        if (string.IsNullOrWhiteSpace(request.EphemeralPublicKey) || string.IsNullOrWhiteSpace(request.SigningPublicKey))
            return Refuse(PairingErrors.MalformedRequest, "请求缺少必要的公钥");

        var name = (hostDisplayName ?? string.Empty).Trim();
        if (name.Length == 0) name = "未命名设备";

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublicKey = Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo());

        var contextDigest = PairingProtocol.ContextDigest(
            request.InviteId, request.GroupId,
            request.DeviceId, request.DisplayName, request.SigningPublicKey, request.AgreementPublicKey,
            request.EphemeralPublicKey, request.Nonce,
            hostIdentity.DeviceId, name, hostIdentity.SigningPublicKey,
            hostIdentity.AgreementPublicKey, ephemeralPublicKey);

        var wrapKey = PairingProtocol.DeriveWrapKey(ephemeral, request.EphemeralPublicKey, contextDigest,
            request.InviteId, request.GroupId, invite.Code);
        var fingerprint = DeviceIdentity.Fingerprint(groupKey);

        string wrappedKey;
        byte[] signature;
        try
        {
            wrappedKey = PairingProtocol.Wrap(wrapKey, contextDigest, groupKey);
            signature = hostIdentity.Sign(PairingProtocol.SignedDigest(contextDigest, wrappedKey, fingerprint));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }

        var response = new PairingResponse(
            request.GroupId,
            hostIdentity.DeviceId,
            name,
            hostIdentity.SigningPublicKey,
            hostIdentity.AgreementPublicKey,
            ephemeralPublicKey,
            wrappedKey,
            fingerprint,
            Convert.ToBase64String(signature));

        return new PairingRespondResult(new PairingOutcome(true, "已生成配对应答", GroupId: request.GroupId,
            KeyFingerprint: fingerprint), response);
    }

    private static PairingRespondResult Refuse(string error, string message) =>
        new(PairingOutcome.Refuse(error, message));
}

/// <summary>
/// A member-side helper for group key rotation.
///
/// Revoking a member is only real if the group key changes afterwards — otherwise the removed
/// device keeps a key that still opens everything. The rotating device wraps the new key to each
/// remaining member with that member's long-term agreement key, so the key can be delivered over
/// whatever channel exists once the member next connects.
/// </summary>
public static class PairingRekey
{
    public static GroupRekeyBlob Seal(string groupId, int keyEpoch, DeviceIdentity hostIdentity,
        string memberAgreementPublicKey, byte[] groupKey)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(groupKey);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var memberKey = DeviceIdentity.ImportAgreementKey(memberAgreementPublicKey);

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"仙灵组密钥 {groupId} {keyEpoch}"));
        var wrapKey = ephemeral.DeriveKeyFromHmac(memberKey.PublicKey, HashAlgorithmName.SHA256, salt, null, null);

        try
        {
            var context = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{groupId}\n{keyEpoch}\n{Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo())}"));

            return new GroupRekeyBlob(groupId, keyEpoch, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),
                PairingProtocol.Wrap(wrapKey, context, groupKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    public static PairingOutcome Accept(DeviceIdentity identity, GroupRekeyBlob blob, out byte[]? groupKey)
    {
        groupKey = null;
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(blob);

        try
        {
            using var ephemeral = DeviceIdentity.ImportAgreementKey(blob.EphemeralPublicKey);
            var privateKey = identity.ExportPrivateKeys().Agreement;

            using var agreement = ECDiffieHellman.Create();
            agreement.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);

            var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"仙灵组密钥 {blob.GroupId} {blob.KeyEpoch}"));
            var wrapKey = agreement.DeriveKeyFromHmac(ephemeral.PublicKey, HashAlgorithmName.SHA256, salt, null, null);

            try
            {
                var context = SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{blob.GroupId}\n{blob.KeyEpoch}\n{blob.EphemeralPublicKey}"));

                if (!PairingProtocol.TryUnwrap(wrapKey, context, blob.WrappedKey, out var key))
                    return PairingOutcome.Refuse(PairingErrors.BadWrap, "组密钥无法解开，可能不是发给本设备的");

                groupKey = key;
                return new PairingOutcome(true, "已接受新的组密钥", GroupId: blob.GroupId,
                    KeyFingerprint: DeviceIdentity.Fingerprint(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return PairingOutcome.Refuse(PairingErrors.BadWrap, "组密钥重发数据无法解析");
        }
    }
}

public sealed record GroupRekeyBlob(string GroupId, int KeyEpoch, string EphemeralPublicKey, string WrappedKey);
