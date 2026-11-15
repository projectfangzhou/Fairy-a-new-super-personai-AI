namespace MyAiAssistant.Services;

/// <summary>A device this machine can send to: it is in one of the paired groups.</summary>
public sealed record TransferPeer(string DeviceId, string DisplayName, string GroupId, string GroupName);

/// <summary>One transfer attempt, as the control surface reports it.</summary>
public sealed record TransferJob(
    string Id,
    string Peer,
    string Direction,
    string State,
    long Bytes,
    string? Error,
    DateTimeOffset StartedAt);

/// <summary>A file that arrived, or is still arriving.</summary>
public sealed record ReceivedFileView(string Name, long Size, bool Completed, string? SavedPath);

/// <summary>
/// The control-centre view of transfers. Deliberately built from plain values rather than the
/// transport types, so the surface cannot accidentally expose frames or key material.
/// </summary>
public sealed record TransferView(
    bool Enabled,
    string? Error,
    string HostDeviceId,
    string DestinationRoot,
    IReadOnlyList<TransferPeer> Peers,
    IReadOnlyList<TransferJob> Jobs,
    IReadOnlyList<ReceivedFileView> Received);

public sealed record TransferCommandResult(bool Ok, string Message);

/// <summary>What the control centre may do with transfers.</summary>
public interface ITransferControl
{
    TransferView Read();

    Task<TransferCommandResult> SendAsync(string deviceId, IReadOnlyList<string> paths);
}
