using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyAiAssistant.Services.Pairing;

/// <summary>
/// Keeps this machine's pairing identity and its device groups.
///
/// Each group has its own 32-byte key, stored wrapped rather than in the clear, and its own epoch.
/// Rotating the key bumps the epoch, which is how a revoked device's old key stops being accepted
/// instead of quietly working forever.
///
/// What is written to disk is deliberately narrow: the identity private keys and one wrapped group
/// key per group. Member display names are labels only; trust decisions are made against the public
/// keys recorded here.
/// </summary>
public sealed class DeviceGroupStore : IDisposable
{
    private const int MaxGroups = 16;
    private const int MaxMembersPerGroup = 32;
    private const int MaxNameLength = 40;
    private const string GroupIdPrefix = "GRP";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly IPairingSecretProtector _protector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly PairingStoreFile _file;

    private DeviceIdentity? _identity;

    public DeviceGroupStore(string directory, IPairingSecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("目录不能为空", nameof(directory));
        _directory = Path.GetFullPath(directory);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        Directory.CreateDirectory(_directory);
        (_file, LastLoadError) = Load();
    }

    /// <summary>Set when the store file could not be read and was set aside; the UI should say so.</summary>
    public string? LastLoadError { get; private set; }

    public string StorePath => Path.Combine(_directory, "pairing.json");

    public string RootDirectory => _directory;

    /// <summary>This machine's pairing identity, created on first use and then reused.</summary>
    public DeviceIdentity Identity
    {
        get
        {
            lock (_gate)
            {
                if (_identity is not null) return _identity;

                if (!string.IsNullOrWhiteSpace(_file.SigningPrivateKey) &&
                    !string.IsNullOrWhiteSpace(_file.AgreementPrivateKey))
                {
                    try
                    {
                        _identity = DeviceIdentity.FromPrivateKeys(
                            _protector.Unprotect(_file.SigningPrivateKey!),
                            _protector.Unprotect(_file.AgreementPrivateKey!));
                    }
                    catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
                    {
                        // A key we cannot read is worse than a new one: it would silently change the
                        // device id and orphan every existing group, so fall through only after
                        // recording why.
                        LastLoadError ??= "本机配对身份无法解密，已生成新的身份（原有配对需要重新配）";
                        _identity = null;
                    }
                }

                if (_identity is null)
                {
                    _identity = DeviceIdentity.Create();
                    var keys = _identity.ExportPrivateKeys();
                    _file.SigningPrivateKey = _protector.Protect(keys.Signing);
                    _file.AgreementPrivateKey = _protector.Protect(keys.Agreement);
                    _file.DeviceId = _identity.DeviceId;
                    Save();
                }

                return _identity;
            }
        }
    }

    public IReadOnlyList<DeviceGroup> Groups
    {
        get
        {
            lock (_gate)
            {
                return _file.Groups
                    .OrderBy(group => group.CreatedAt)
                    .Select(Map)
                    .ToArray();
            }
        }
    }

    public string? ActiveGroupId
    {
        get
        {
            lock (_gate) return _file.ActiveGroupId;
        }
    }

    public DeviceGroup? ActiveGroup
    {
        get
        {
            lock (_gate)
            {
                var record = _file.Groups.FirstOrDefault(group => group.GroupId == _file.ActiveGroupId);
                return record is null ? null : Map(record);
            }
        }
    }

    public DeviceGroup? Find(string groupId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            return record is null ? null : Map(record);
        }
    }

    public (bool Ok, string Message, DeviceGroup? Group) CreateGroup(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return (false, "设备组名称不能为空", null);
        if (trimmed.Length > MaxNameLength) return (false, $"设备组名称最多 {MaxNameLength} 个字符", null);

        lock (_gate)
        {
            if (_file.Groups.Count >= MaxGroups)
                return (false, $"最多只能有 {MaxGroups} 个设备组", null);

            var groupId = GroupIdPrefix + Base32.Encode(RandomNumberGenerator.GetBytes(10));
            var record = new PairingGroupRecord
            {
                GroupId = groupId,
                Name = trimmed,
                CreatedAt = _clock(),
                WrappedGroupKey = _protector.Protect(Convert.ToBase64String(PairingProtocol.NewGroupKey())),
                KeyEpoch = 1
            };

            _file.Groups.Add(record);
            _file.ActiveGroupId ??= groupId;
            Save();

            return (true, $"已创建设备组「{trimmed}」", Map(record));
        }
    }

    /// <summary>Adds a joined device. Its capabilities start empty: pairing is not authorisation.</summary>
    public (bool Ok, string Message, GroupMember? Member) AddMember(string groupId, PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var record = FindRecord(groupId);
            if (record is null) return (false, "设备组不存在", null);

            if (record.Members.Any(member => member.DeviceId == request.DeviceId))
                return (false, "这台设备已经在该设备组里了", null);

            if (record.Members.Count >= MaxMembersPerGroup)
                return (false, $"每个设备组最多 {MaxMembersPerGroup} 台设备", null);

            var name = (request.DisplayName ?? string.Empty).Trim();
            if (name.Length == 0) name = "未命名设备";

            var member = new PairingMemberRecord
            {
                DeviceId = request.DeviceId,
                DisplayName = name,
                SigningPublicKey = request.SigningPublicKey,
                AgreementPublicKey = request.AgreementPublicKey,
                JoinedAt = _clock()
            };

            record.Members.Add(member);
            Save();

            return (true, $"已加入「{name}」", MapMember(member));
        }
    }

    /// <summary>
    /// Removes a member and rotates the group key in one step.
    ///
    /// Both halves matter: a removed device that still holds the old key could keep reading
    /// anything encrypted with it, so the key is replaced and the epoch moves on. The new key is
    /// sealed to each remaining member and returned, ready to deliver over whatever channel that
    /// device connects on next.
    /// </summary>
    public (bool Ok, string Message, int KeyEpoch, IReadOnlyList<GroupRekeyBlob> Rekeys) RemoveMember(
        string groupId, string deviceId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            if (record is null) return (false, "设备组不存在", 0, Array.Empty<GroupRekeyBlob>());

            var member = record.Members.FirstOrDefault(entry => entry.DeviceId == deviceId);
            if (member is null) return (false, "该设备不在这个组里", record.KeyEpoch, Array.Empty<GroupRekeyBlob>());

            record.Members.Remove(member);

            var rekeys = RotateLocked(record);
            Save();

            return (true, $"已移除「{member.DisplayName}」并更换了组密钥（第 {record.KeyEpoch} 代）",
                record.KeyEpoch, rekeys);
        }
    }

    /// <summary>Replaces the group key without removing anyone, e.g. after a device was lost.</summary>
    public (bool Ok, string Message, int KeyEpoch, IReadOnlyList<GroupRekeyBlob> Rekeys) RotateGroupKey(string groupId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            if (record is null) return (false, "设备组不存在", 0, Array.Empty<GroupRekeyBlob>());

            var rekeys = RotateLocked(record);
            Save();

            return (true, $"组密钥已更换为第 {record.KeyEpoch} 代", record.KeyEpoch, rekeys);
        }
    }

    public (bool Ok, string Message) Grant(string groupId, string deviceId, PairingCapability capability)
    {
        lock (_gate)
        {
            var (record, member, error) = LocateMember(groupId, deviceId);
            if (record is null || member is null) return (false, error);

            if (!Enum.IsDefined(capability)) return (false, "未知的能力");

            if (member.Capabilities.Contains(capability.ToString()))
                return (true, $"「{member.DisplayName}」已经有「{capability.Describe()}」能力");

            member.Capabilities.Add(capability.ToString());
            Save();

            var suffix = capability.RequiresPerCallApproval() ? "，但每次调用仍需你当场确认" : string.Empty;
            return (true, $"已授予「{member.DisplayName}」「{capability.Describe()}」{suffix}");
        }
    }

    public (bool Ok, string Message) Withdraw(string groupId, string deviceId, PairingCapability capability)
    {
        lock (_gate)
        {
            var (record, member, error) = LocateMember(groupId, deviceId);
            if (record is null || member is null) return (false, error);

            // Historical grants may name a capability that no longer exists; treat that as already withdrawn.
            if (!member.Capabilities.Remove(capability.ToString()))
                return (true, $"「{member.DisplayName}」本来就没有「{capability.Describe()}」能力");

            Save();
            return (true, $"已收回「{member.DisplayName}」的「{capability.Describe()}」能力");
        }
    }

    public bool Allows(string groupId, string deviceId, PairingCapability capability)
    {
        lock (_gate)
        {
            var (_, member, _) = LocateMember(groupId, deviceId);
            return member is not null && member.Capabilities.Contains(capability.ToString());
        }
    }

    /// <summary>Chooses which group requests apply to when this device is in more than one.</summary>
    public (bool Ok, string Message) SetActiveGroup(string groupId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            if (record is null) return (false, "设备组不存在");

            _file.ActiveGroupId = record.GroupId;
            Save();
            return (true, $"当前设备组已切换为「{record.Name}」");
        }
    }

    public GroupIsolation IsolationFor(string groupId)
    {
        lock (_gate)
        {
            if (FindRecord(groupId) is null) throw new InvalidOperationException("设备组不存在");
        }

        return new GroupIsolation(_directory, groupId);
    }

    /// <summary>The group key in the clear, for the caller to use and then let go of.</summary>
    public byte[]? TryGetGroupKey(string groupId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            return record is null ? null : UnwrapGroupKey(record);
        }
    }

    public string GroupKeyFingerprint(string groupId)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            if (record is null) return string.Empty;

            var cacheKey = record.GroupId + "#" + record.KeyEpoch;
            if (_fingerprints.TryGetValue(cacheKey, out var cached)) return cached;

            var key = UnwrapGroupKey(record);
            var fingerprint = DeviceIdentity.Fingerprint(key);
            CryptographicOperations.ZeroMemory(key);
            _fingerprints[cacheKey] = fingerprint;
            return fingerprint;
        }
    }

    /// <summary>Payloads stamped with an older epoch come from a key this group has already replaced.</summary>
    public bool AcceptsEpoch(string groupId, int keyEpoch)
    {
        lock (_gate)
        {
            var record = FindRecord(groupId);
            return record is not null && record.KeyEpoch == keyEpoch;
        }
    }

    private (PairingGroupRecord? Record, PairingMemberRecord? Member, string Error) LocateMember(
        string groupId, string deviceId)
    {
        var record = FindRecord(groupId);
        if (record is null) return (null, null, "设备组不存在");

        var member = record.Members.FirstOrDefault(entry => entry.DeviceId == deviceId);
        return member is null ? (record, null, "该设备不在这个组里") : (record, member, string.Empty);
    }

    private List<GroupRekeyBlob> RotateLocked(PairingGroupRecord record)
    {
        var groupKey = PairingProtocol.NewGroupKey();
        record.KeyEpoch++;
        record.WrappedGroupKey = _protector.Protect(Convert.ToBase64String(groupKey));
        _fingerprints.Remove(record.GroupId + "#" + (record.KeyEpoch - 1));

        var blobs = new List<GroupRekeyBlob>();
        try
        {
            foreach (var member in record.Members)
            {
                if (string.IsNullOrWhiteSpace(member.AgreementPublicKey)) continue;

                try
                {
                    blobs.Add(PairingRekey.Seal(record.GroupId, record.KeyEpoch, Identity,
                        member.AgreementPublicKey, groupKey));
                }
                catch (CryptographicException)
                {
                    // A member whose key cannot be used simply does not get a blob; it stays on the
                    // old epoch and its requests will be refused until it pairs again.
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(groupKey);
        }

        return blobs;
    }

    private PairingGroupRecord? FindRecord(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return null;
        return _file.Groups.FirstOrDefault(group => group.GroupId == groupId);
    }

    private byte[] UnwrapGroupKey(PairingGroupRecord record) =>
        Convert.FromBase64String(_protector.Unprotect(record.WrappedGroupKey));

    private DeviceGroup Map(PairingGroupRecord record) => new(
        record.GroupId,
        record.Name,
        record.CreatedAt,
        record.KeyEpoch,
        GroupKeyFingerprint(record.GroupId),
        record.Members.Select(MapMember).ToArray());

    private static GroupMember MapMember(PairingMemberRecord member) => new(
        member.DeviceId,
        member.DisplayName,
        member.SigningPublicKey,
        member.AgreementPublicKey,
        member.JoinedAt,
        member.Capabilities
            .Select(name => Enum.TryParse<PairingCapability>(name, out var capability)
                ? capability
                : (PairingCapability?)null)
            .Where(capability => capability is not null)
            .Select(capability => capability!.Value)
            .ToArray());

    private (PairingStoreFile File, string? Error) Load()
    {
        if (!File.Exists(StorePath)) return (new PairingStoreFile(), null);

        try
        {
            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<PairingStoreFile>(json, JsonOptions) ?? new PairingStoreFile();
            return (parsed, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Do not overwrite what we could not read: set it aside so it can be inspected, and
            // report it, because the alternative is losing the identity without a trace.
            var quarantine = StorePath + ".unreadable-" + _clock().ToUnixTimeSeconds();
            try
            {
                File.Copy(StorePath, quarantine, overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                // Best effort only.
            }

            return (new PairingStoreFile(),
                $"配对数据无法读取（已另存为 {Path.GetFileName(quarantine)}）：{ex.GetType().Name}");
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_file, JsonOptions);
        var temporary = StorePath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, StorePath, overwrite: true);
    }

    public void Dispose() => _identity?.Dispose();
}

internal sealed class PairingStoreFile
{
    public int Version { get; set; } = 1;

    public string? DeviceId { get; set; }

    public string? SigningPrivateKey { get; set; }

    public string? AgreementPrivateKey { get; set; }

    public string? ActiveGroupId { get; set; }

    public List<PairingGroupRecord> Groups { get; set; } = new();
}

internal sealed class PairingGroupRecord
{
    public string GroupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string WrappedGroupKey { get; set; } = string.Empty;

    public int KeyEpoch { get; set; } = 1;

    public List<PairingMemberRecord> Members { get; set; } = new();
}

internal sealed class PairingMemberRecord
{
    public string DeviceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string SigningPublicKey { get; set; } = string.Empty;

    public string AgreementPublicKey { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }

    public List<string> Capabilities { get; set; } = new();
}
