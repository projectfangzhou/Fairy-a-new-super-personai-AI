namespace MyAiAssistant.Services.Transfer;

/// <summary>
/// A bidirectional byte pipe plus the signalling needed to establish it.
///
/// The session logic is written against this rather than against WebRTC directly: the same
/// framing, approval and verification apply whether the bytes arrive over a data channel or over
/// anything else that can carry them, and the transport can be swapped without touching them.
/// </summary>
public interface ITransferChannel : IAsyncDisposable
{
    /// <summary>Signalling this side needs to hand to the peer. Nothing arrives here once open.</summary>
    event Action<TransferSignal>? SignalOutbound;

    event Action? ChannelOpened;

    event Action<byte[]>? BytesReceived;

    event Action<string>? Failed;

    bool IsOpen { get; }

    Task StartAsync(WebRtcRole role);

    Task ApplySignalAsync(TransferSignal signal);

    Task SendAsync(ReadOnlyMemory<byte> data);
}
