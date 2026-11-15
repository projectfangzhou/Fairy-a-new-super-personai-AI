using System.Text.Json;

namespace MyAiAssistant.Services.Transfer;

public enum WebRtcRole
{
    Offerer = 0,
    Answerer
}

public enum TransferSignalKind
{
    Offer = 0,
    Answer,
    Ice
}

/// <summary>
/// One signalling message: the SDP offer, the answer, or a single ICE candidate. These are the
/// bytes that have to reach the other device before any direct connection exists — which is why
/// they travel over the already paired, group-encrypted channel and never through a public relay.
/// </summary>
public sealed record TransferSignal(TransferSignalKind Kind, string Payload)
{
    public string ToJson() => JsonSerializer.Serialize(new { kind = Kind.ToString(), payload = Payload });

    /// <summary>Parses a signal from the wire. Unknown or malformed input yields null, never an exception.</summary>
    public static TransferSignal? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var kindText = root.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
            var payload = root.TryGetProperty("payload", out var value) ? value.GetString() : null;
            if (kindText is null || payload is null) return null;

            return Enum.TryParse<TransferSignalKind>(kindText, ignoreCase: true, out var parsed)
                ? new TransferSignal(parsed, payload)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
