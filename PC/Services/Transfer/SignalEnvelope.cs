using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.Transfer;

/// <summary>
/// A signalling message on its way between two paired devices.
///
/// It is sealed with the group key, and the routing fields are the associated data, so a message
/// cannot be re-addressed to another device or replayed under a different timestamp without the
/// seal failing. That is what lets the signalling travel through the host's plain HTTP endpoint
/// without a LAN eavesdropper learning the SDP, and without any device being able to forge one.
/// </summary>
public sealed record SignalEnvelope(string GroupId, string From, string To, long SentAt, string Nonce, string Sealed)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SignalEnvelope? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<SignalEnvelope>(json, JsonOptions);
            if (parsed is null) return null;

            return string.IsNullOrWhiteSpace(parsed.GroupId) || string.IsNullOrWhiteSpace(parsed.From) ||
                   string.IsNullOrWhiteSpace(parsed.To) || string.IsNullOrWhiteSpace(parsed.Nonce) ||
                   string.IsNullOrWhiteSpace(parsed.Sealed)
                ? null
                : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class TransferSignals
{
    /// <summary>Signals are small; anything bigger is not an SDP and is refused before it is opened.</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    /// <summary>Clocks on a LAN are not perfectly aligned, so a small window is allowed either way.</summary>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(2);

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>What a sealed delivery carries: the signal plus who sent it, for the record.</summary>
    public sealed record Payload(string From, string Kind, string Value);

    public static SignalEnvelope Seal(byte[] groupKey, string groupId, string from, string to, TransferSignal signal,
        DateTimeOffset now, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(signal);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));

        var sentAt = now.ToUnixTimeMilliseconds();
        var nonceValue = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();

        var payload = JsonSerializer.Serialize(new Payload(from, signal.Kind.ToString(), signal.Payload));
        var sealedValue = SealText(groupKey, AssociatedData(groupId, from, to, sentAt, nonceValue), payload);

        return new SignalEnvelope(groupId, from, to, sentAt, nonceValue, sealedValue);
    }

    /// <summary>
    /// Opens an envelope and checks it is fresh, addressed to us, and inside our group. Every
    /// failure returns a reason instead of throwing, because this runs on network input.
    /// </summary>
    public static bool TryOpen(byte[] groupKey, SignalEnvelope envelope, DateTimeOffset now, string expectedTo,
        out string kind, out string value, out string error)
    {
        kind = string.Empty;
        value = string.Empty;

        if (groupKey.Length != 32)
        {
            error = "组密钥长度不对";
            return false;
        }

        if (!string.Equals(envelope.To, expectedTo, StringComparison.Ordinal))
        {
            error = "消息不是发给本设备的";
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(envelope.SentAt);
        if (age > FreshnessWindow || age < -FreshnessWindow)
        {
            error = "消息时间戳超出允许范围";
            return false;
        }

        var aad = AssociatedData(envelope.GroupId, envelope.From, envelope.To, envelope.SentAt, envelope.Nonce);
        if (!TryOpenText(groupKey, aad, envelope.Sealed, out var plaintext, out var reason))
        {
            error = reason;
            return false;
        }

        if (plaintext.Length > MaxPayloadBytes)
        {
            error = "信令内容过大";
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(plaintext);
        }
        catch (JsonException)
        {
            error = "信令内容无法解析";
            return false;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Kind))
        {
            error = "信令类型未知";
            return false;
        }

        kind = payload.Kind;
        value = payload.Value;
        error = string.Empty;
        return true;
    }

    /// <summary>Opens an envelope that must carry an offer, an answer or an ICE candidate.</summary>
    public static bool TryOpenSignal(byte[] groupKey, SignalEnvelope envelope, DateTimeOffset now, string expectedTo,
        out TransferSignal? signal, out string error)
    {
        signal = null;

        if (!TryOpen(groupKey, envelope, now, expectedTo, out var kind, out var value, out error)) return false;

        if (!Enum.TryParse<TransferSignalKind>(kind, ignoreCase: true, out var parsed))
        {
            error = $"这不是连接信令：{kind}";
            return false;
        }

        signal = new TransferSignal(parsed, value);
        return true;
    }

    /// <summary>Seals a small control request — currently only "which signal ids have I already seen".</summary>
    public static SignalEnvelope SealControl(byte[] groupKey, string groupId, string from, string to, string controlKind,
        string controlValue, DateTimeOffset now, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));

        var sentAt = now.ToUnixTimeMilliseconds();
        var nonceValue = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();
        var payload = JsonSerializer.Serialize(new Payload(from, controlKind, controlValue));

        return new SignalEnvelope(groupId, from, to, sentAt, nonceValue,
            SealText(groupKey, AssociatedData(groupId, from, to, sentAt, nonceValue), payload));
    }

    public static string SealText(byte[] groupKey, string associatedData, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(groupKey, TagBytes))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag, Encoding.UTF8.GetBytes(associatedData));
        }

        var blob = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceBytes);
        cipher.CopyTo(blob, NonceBytes + TagBytes);
        return Convert.ToBase64String(blob);
    }

    public static bool TryOpenText(byte[] groupKey, string associatedData, string sealedText, out string plaintext, out string error)
    {
        plaintext = string.Empty;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(sealedText);
        }
        catch (FormatException)
        {
            error = "密封内容不是合法的 base64";
            return false;
        }

        if (blob.Length <= NonceBytes + TagBytes)
        {
            error = "密封内容过短";
            return false;
        }

        var cipherLength = blob.Length - NonceBytes - TagBytes;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(groupKey, TagBytes);
            aes.Decrypt(blob.AsSpan(0, NonceBytes), blob.AsSpan(NonceBytes + TagBytes, cipherLength),
                blob.AsSpan(NonceBytes, TagBytes), plain, Encoding.UTF8.GetBytes(associatedData));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            error = "密封内容校验失败（密钥不符或内容被改动）";
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plain);
        error = string.Empty;
        return true;
    }

    private static string AssociatedData(string groupId, string from, string to, long sentAt, string nonce) =>
        $"{groupId}\n{from}\n{to}\n{sentAt}\n{nonce}";
}

/// <summary>
/// Remembers which nonces have been used recently, so a message that was captured on the wire
/// cannot be delivered a second time inside the freshness window.
/// </summary>
public sealed class SignalReplayGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public SignalReplayGuard(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>False when this nonce was already used; the caller refuses the message.</summary>
    public bool Accept(string groupId, string from, string nonce)
    {
        var key = groupId + "|" + from + "|" + nonce;
        var now = _clock();

        lock (_gate)
        {
            foreach (var stale in _seen.Where(pair => now - pair.Value > TransferSignals.FreshnessWindow)
                         .Select(pair => pair.Key).ToArray())
            {
                _seen.Remove(stale);
            }

            if (_seen.ContainsKey(key)) return false;

            _seen[key] = now;
            return true;
        }
    }
}
