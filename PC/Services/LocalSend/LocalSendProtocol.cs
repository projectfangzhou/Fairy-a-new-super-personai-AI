using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>
/// The parts of LocalSend v2 that are pure: the announce payload, its validation, and the
/// fingerprint. Interoperability fails on small things — a wrong port type, a missing version,
/// a device type the other side does not know — so this stays a strict, testable mapping
/// instead of "something that looks like JSON".
/// </summary>
public static class LocalSendProtocol
{
    public const string Version = "2.0";
    public const int DefaultPort = 53317;
    public const string DeviceTypeDesktop = "desktop";
    public const string DeviceTypeMobile = "mobile";
    public const string DeviceTypeWeb = "web";
    public const string DeviceTypeHeadless = "headless";
    public const string DeviceTypeServer = "server";

    public static readonly string MulticastGroup = "224.0.0.167";
    public static readonly int MulticastPort = DefaultPort;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The published protocol uses camelCase field names; emitting "Alias" would make the
        // announce unreadable to the official app.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string BuildAnnounce(LocalSendDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var payload = new AnnouncePayload(
            device.Alias,
            string.IsNullOrWhiteSpace(device.Version) ? Version : device.Version,
            device.DeviceModel,
            NormalizeDeviceType(device.DeviceType),
            device.Fingerprint,
            device.Port,
            string.IsNullOrWhiteSpace(device.Protocol) ? "http" : device.Protocol,
            device.Download);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed record AnnouncePayload(
        string Alias, string Version, string DeviceModel, string DeviceType,
        string Fingerprint, int Port, string Protocol, bool Download);

    /// <summary>
    /// Parses an announce datagram. Returns null rather than a half-filled device: a peer we
    /// cannot address or identify is not a peer.
    /// </summary>
    public static LocalSendDevice? ParseAnnounce(string json, string? sourceAddress)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var alias = ReadString(root, "alias");
            var fingerprint = ReadString(root, "fingerprint");
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(fingerprint)) return null;

            var port = ReadPort(root);
            if (port is null) return null;

            var protocol = ReadString(root, "protocol") ?? "http";
            if (protocol is not ("http" or "https")) return null;

            return new LocalSendDevice(
                alias,
                ReadString(root, "version") ?? Version,
                ReadString(root, "deviceModel") ?? "Unknown",
                NormalizeDeviceType(ReadString(root, "deviceType")),
                fingerprint,
                port.Value,
                protocol,
                ReadBool(root, "download")) { Address = sourceAddress };
        }
    }

    private static int? ReadPort(JsonElement root)
    {
        if (!root.TryGetProperty("port", out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is > 0 and <= 65535 => number,
            // Some clients send the port as a string; accept it, but never invent one.
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static string NormalizeDeviceType(string? deviceType) => deviceType switch
    {
        DeviceTypeMobile => DeviceTypeMobile,
        DeviceTypeWeb => DeviceTypeWeb,
        DeviceTypeHeadless => DeviceTypeHeadless,
        DeviceTypeServer => DeviceTypeServer,
        DeviceTypeDesktop => DeviceTypeDesktop,
        _ => DeviceTypeDesktop
    };

    /// <summary>
    /// A stable per-install identity. LocalSend expects a random fingerprint, not the machine
    /// name: two machines with the same hostname are still different devices.
    /// </summary>
    public static string CreateFingerprint()
    {
        var material = $"{Environment.MachineName}|{Guid.NewGuid():N}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>Guesses the MIME type LocalSend shows on the other side. Wrong values only cost a label.</summary>
    public static string GuessFileType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" or ".log" => "text/plain",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".exe" or ".msi" => "application/octet-stream",
        _ => "application/octet-stream"
    };

    /// <summary>Builds the file map for prepare-upload, keyed by the id the receiver echoes back.</summary>
    public static Dictionary<string, LocalSendFile> DescribeFiles(IEnumerable<string> paths)
    {
        var files = new Dictionary<string, LocalSendFile>(StringComparer.Ordinal);
        var index = 0;
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var id = (++index).ToString(CultureInfo.InvariantCulture);
            files[id] = new LocalSendFile(id, info.Name, info.Length, GuessFileType(info.Name));
        }
        return files;
    }
}
