namespace MyAiAssistant.Services;

/// <summary>The control-centre view of LAN sending: who is around, and how a send went.</summary>
public sealed record LocalSendPeer(
    string Fingerprint,
    string Alias,
    string DeviceType,
    string? Endpoint,
    DateTimeOffset LastSeen);

public sealed record LocalSendSendResult(bool Ok, string Message, int UploadedFiles = 0);

/// <summary>
/// Discovered peers plus the ability to send to one of them. An interface so the control centre
/// can be tested without multicast sockets or a real peer on the network.
/// </summary>
public interface ILocalSendPeers
{
    bool DiscoveryRunning { get; }

    IReadOnlyList<LocalSendPeer> Peers();

    Task<LocalSendSendResult> SendAsync(string fingerprint, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
