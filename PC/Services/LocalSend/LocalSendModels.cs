using System.Text.Json.Serialization;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>
/// Wire shapes of the LocalSend v2 protocol. Kept as data only: the same structs are used to
/// speak to the official app, so field names must match the published protocol exactly.
/// </summary>
public sealed record LocalSendDevice(
    string Alias,
    string Version,
    string DeviceModel,
    string DeviceType,
    string Fingerprint,
    int Port,
    string Protocol,
    bool Download)
{
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The endpoint peers call back on, derived from the datagram source address.</summary>
    public string? Address { get; init; }

    public string? Endpoint => Address is null ? null : $"{Protocol}://{Address}:{Port}";
}

public sealed record LocalSendFile(
    string Id,
    string FileName,
    long Size,
    string FileType,
    string? Sha256 = null);

// The protocol uses a bare map of file id to file object, not a named array.
public sealed class LocalSendPrepareRequest
{
    [JsonPropertyName("info")]
    public LocalSendDeviceInfo? Info { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, LocalSendFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed record LocalSendDeviceInfo(
    string Alias,
    string Version,
    string DeviceModel,
    string DeviceType,
    string Fingerprint,
    int Port,
    string Protocol,
    bool Download);

public sealed class LocalSendPrepareResponse
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed record LocalSendResult(bool Ok, string Message, string? SessionId = null, int UploadedFiles = 0);
