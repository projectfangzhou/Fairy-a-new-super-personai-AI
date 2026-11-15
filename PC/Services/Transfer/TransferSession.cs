using System.IO;

namespace MyAiAssistant.Services.Transfer;

/// <summary>What the user is being asked to accept, before a single byte is written.</summary>
public sealed record TransferOffer(TransferManifest Manifest)
{
    public long TotalBytes => Manifest.Files.Sum(file => Math.Max(0, file.Size));

    public string Describe() =>
        $"{Manifest.Sender} 想发送 {Manifest.Files.Count} 个文件（{Format(TotalBytes)}）";

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}

/// <summary>
/// A refusal, and whether trying again could help.
///
/// The distinction is the difference between "this file cannot be accepted here" and "the link
/// dropped": only the second is worth another attempt, and a retry continues the same transfer
/// rather than sending everything again.
/// </summary>
public sealed record TransferRefusal(string Message, bool Retryable);

/// <summary>
/// One transfer, from either end.
///
/// The rule this class exists to enforce: nothing is written until the user has said yes to a
/// manifest, and nothing is published under its real name until the whole file has matched its
/// hash. A transfer that is refused, cancelled or interrupted leaves no file behind — a half
/// received file that looks complete is worse than no file at all.
/// </summary>
public sealed class TransferSession : IAsyncDisposable
{
    /// <summary>
    /// How long to wait for the receiver to accept the manifest. This is deliberately generous: the
    /// wait is usually a person deciding, and sending chunks before that decision only wastes the
    /// link because the receiver has nothing to write them into yet.
    /// </summary>
    private static readonly TimeSpan AcceptanceWait = TimeSpan.FromMinutes(5);

    private readonly ITransferChannel _channel;
    private readonly byte[] _groupKey;
    private readonly TransferReceiver _receiver;
    private readonly Func<TransferOffer, Task<bool>> _approveIncoming;
    private readonly SemaphoreSlim _inbound = new(1, 1);

    private readonly TimeSpan _acceptanceWait;
    private readonly TimeSpan _completionWait;
    private TaskCompletionSource<Acceptance>? _acceptance;
    private TaskCompletionSource<TransferCompletion>? _completion;
    private bool _completionSent;

    public TransferSession(ITransferChannel channel, byte[] groupKey, string destinationRoot,
        Func<TransferOffer, Task<bool>>? approveIncoming = null, int chunkSize = TransferFrames.MaxPayloadBytes,
        TimeSpan? acceptanceWait = null, TimeSpan? completionWait = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentNullException.ThrowIfNull(groupKey);

        // Own a copy: callers are expected to wipe the array they passed in as soon as the session
        // exists, and a session that borrowed the reference would start sealing frames with zeros.
        _groupKey = (byte[])groupKey.Clone();
        _receiver = new TransferReceiver(_groupKey, destinationRoot, chunkSize);

        // Fail closed: without a way to ask, incoming transfers are refused rather than accepted.
        _approveIncoming = approveIncoming ?? (_ => Task.FromResult(false));
        _acceptanceWait = acceptanceWait ?? AcceptanceWait;
        _completionWait = completionWait ?? TimeSpan.FromMinutes(2);

        _channel.SignalOutbound += signal => OutboundSignal?.Invoke(signal);
        _channel.BytesReceived += OnBytes;
        _channel.Failed += message => LastError = message;
        _channel.ChannelOpened += () => ChannelOpened?.Invoke();
    }

    /// <summary>Signalling to hand to the peer through the paired channel.</summary>
    public event Action<TransferSignal>? OutboundSignal;

    public event Action? ChannelOpened;

    /// <summary>Raised when a file passed its hash check and was moved into the destination.</summary>
    public event Action<TransferFileProgress>? FileReceived;

    /// <summary>Raised for anything that stopped a transfer, with the reason and whether to retry.</summary>
    public event Action<TransferRefusal>? Refused;

    public bool IsOpen => _channel.IsOpen;

    public string? LastError { get; private set; }

    public TransferManifest? IncomingManifest { get; private set; }

    /// <summary>True when the manifest continued a transfer that had already begun.</summary>
    public bool Resumed => _receiver.Resumed;

    public IReadOnlyList<TransferFileProgress> Progress => _receiver.Progress;

    public async Task<bool> OpenAsync(WebRtcRole role, TimeSpan? timeout = null)
    {
        if (!await StartChannelAsync(role)) return false;
        return _channel.IsOpen || await WaitForChannelAsync(timeout ?? TimeSpan.FromSeconds(40));
    }

    /// <summary>
    /// Starts the channel without waiting for it to open. The answering side needs this: it has to
    /// be ready to receive the offer before the offer can arrive.
    /// </summary>
    public async Task<bool> StartChannelAsync(WebRtcRole role)
    {
        try
        {
            await _channel.StartAsync(role);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> WaitForChannelAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_channel.IsOpen) return true;
            await Task.Delay(50);
        }

        return _channel.IsOpen;
    }

    public Task DeliverSignalAsync(TransferSignal signal) => _channel.ApplySignalAsync(signal);

    /// <summary>
    /// Sends files over an open channel; returns the number of bytes put on the wire.
    ///
    /// After the manifest it waits briefly for the receiver's resume plan. A receiver that already
    /// holds part of this transfer answers with what it is missing, and only those chunks go out —
    /// which is what makes a retry continue instead of restart.
    /// </summary>
    public async Task<long> SendFilesAsync(IReadOnlyList<string> paths, string senderName,
        CancellationToken cancellationToken = default, string? sessionId = null)
    {
        if (!_channel.IsOpen) throw new InvalidOperationException("数据通道尚未建立");

        var sender = new TransferSender(_groupKey, paths, senderName, _receiver.ChunkSize, sessionId);

        // Register the wait before sending: a receiver that refuses immediately can answer before
        // this method resumes, and that answer must not be dropped.
        var wait = new TaskCompletionSource<Acceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptance = wait;

        Acceptance acceptance;
        try
        {
            await _channel.SendAsync(sender.BuildManifestFrame());
            acceptance = await WaitForAcceptanceAsync(wait, _acceptanceWait);
        }
        finally
        {
            _acceptance = null;
        }
        if (!acceptance.Accepted)
        {
            Refuse(acceptance.Reason ?? "对方没有接受这次传输", retryable: false);
            throw new InvalidOperationException(acceptance.Reason ?? "对方没有接受这次传输");
        }

        var plan = acceptance.Plan;
        var usePlan = plan is not null &&
                      string.Equals(plan.SessionId, sender.Manifest.SessionId, StringComparison.Ordinal);
        // Register the final confirmation before sending anything, for the same reason as above.
        var completionWait = new TaskCompletionSource<TransferCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completionWait;

        var sent = 0L;

        for (var fileIndex = 0; fileIndex < sender.Manifest.Files.Count; fileIndex++)
        {
            var chunkCount = sender.ChunkCount(fileIndex);
            var missing = usePlan
                ? plan!.MissingFor(fileIndex, chunkCount)
                : Enumerable.Range(0, chunkCount).ToArray();

            foreach (var chunkIndex in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = sender.BuildChunkFrame(fileIndex, chunkIndex);
                if (frame is null) continue;

                await _channel.SendAsync(frame);
                sent += frame.Length;
            }

            await _channel.SendAsync(sender.BuildCompleteFrame(fileIndex));
        }

        try
        {
            // "Sent" has to mean the receiver says the files are on disk, not that the frames left.
            var completion = await WaitForCompletionAsync(completionWait, _completionWait);
            if (completion is null)
            {
                Refuse("对方没有确认收到文件", retryable: true);
                throw new InvalidOperationException("对方没有确认收到文件");
            }

            return sent;
        }
        finally
        {
            _completion = null;
        }
    }

    private void OnBytes(byte[] data) => _ = HandleInboundAsync(data);

    private async Task HandleInboundAsync(byte[] data)
    {
        // Overlapping frames would let a manifest be adopted while an earlier file is still being
        // written, so inbound frames are handled strictly one at a time.
        await _inbound.WaitAsync();
        try
        {
            if (!TransferFrames.TryDecode(data, _groupKey, out var frame, out var error) || frame is null)
            {
                // A frame that fails authentication will fail again; retrying cannot help.
                Refuse($"{error}（长度 {data.Length} 字节）", retryable: false);
                return;
            }

            switch (frame.Kind)
            {
                case TransferFrameKind.Manifest:
                    await HandleManifestAsync(data, frame);
                    break;

                case TransferFrameKind.Chunk:
                {
                    var result = _receiver.Accept(data);
                    if (!result.Ok) Refuse(result.Message, retryable: true);
                    break;
                }

                case TransferFrameKind.Complete:
                {
                    var result = _receiver.Accept(data);
                    if (!result.Ok)
                    {
                        Refuse(result.Message, retryable: true);
                        break;
                    }

                    var progress = _receiver.Progress[frame.FileIndex];
                    if (progress.Completed)
                    {
                        FileReceived?.Invoke(progress);

                        // Every file is on disk: tell the sender, so it can stop waiting.
                        if (_receiver.Progress.All(file => file.Completed)) await SendCompletionAsync();
                    }
                    break;
                }

                case TransferFrameKind.Ack:
                {
                    // The same frame kind carries both answers: what the receiver already holds, and
                    // its final word that the files are on disk.
                    var completion = TransferCompletion.Parse(frame.Payload);
                    if (completion is not null)
                    {
                        _completion?.TrySetResult(completion);
                        break;
                    }

                    var plan = TransferResumePlan.Parse(frame.Payload);
                    if (plan is not null) _acceptance?.TrySetResult(new Acceptance(true, plan, null));
                    break;
                }

                case TransferFrameKind.Cancel:
                    _acceptance?.TrySetResult(new Acceptance(false, null, "对端取消了这次传输"));
                    Refuse("对端取消了这次传输", retryable: false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Refuse($"接收失败：{ex.GetType().Name}", retryable: true);
        }
        finally
        {
            _inbound.Release();
        }
    }

    private async Task HandleManifestAsync(byte[] data, TransferFrame frame)
    {
        var manifest = TransferFrames.ParseManifest(frame);
        if (manifest is null)
        {
            Refuse("清单无法解析", retryable: false);
            return;
        }

        bool approved;
        try
        {
            approved = await _approveIncoming(new TransferOffer(manifest));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            approved = false;
        }

        if (!approved)
        {
            // A person decided; asking again would be asking the same question twice.
            _acceptance?.TrySetResult(new Acceptance(false, null, $"{manifest.Sender} 的传输没有被接受"));
            Refuse($"{manifest.Sender} 的传输没有被接受", retryable: false);
            await SendCancelAsync();
            return;
        }

        // The receiver validates names, sizes and destination state; approving does not make an
        // unacceptable manifest acceptable.
        var accepted = _receiver.Accept(data);
        if (!accepted.Ok)
        {
            _acceptance?.TrySetResult(new Acceptance(false, null, accepted.Message));
            Refuse(accepted.Message, retryable: false);
            await SendCancelAsync();
            return;
        }

        IncomingManifest = manifest;

        // A session can carry more than one transfer over its life; the confirmation belongs to the
        // transfer that just started, not to the previous one.
        _completionSent = false;

        // Tell the sender exactly what is missing. Even a first attempt answers — an empty plan
        // tells it the receiver is ready rather than making it wait for the timeout.
        await SendResumePlanAsync();
    }

    private async Task SendResumePlanAsync()
    {
        var plan = _receiver.ResumePlan();
        if (plan is null) return;

        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(plan.ToJson());
            await _channel.SendAsync(TransferFrames.Encode(TransferFrameKind.Ack, -1, -1, payload, _groupKey));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The sender will fall back to sending everything, which is correct, just wasteful.
        }
    }

    private static async Task<Acceptance> WaitForAcceptanceAsync(TaskCompletionSource<Acceptance> wait, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(wait.Task, Task.Delay(timeout));
        return completed == wait.Task
            ? wait.Task.Result
            : new Acceptance(false, null, "等待对方接受超时");
    }

    /// <summary>Whether the peer took the manifest, and what it already holds if it did.</summary>
    private sealed record Acceptance(bool Accepted, TransferResumePlan? Plan, string? Reason);

    private async Task SendCompletionAsync()
    {
        if (_completionSent || IncomingManifest is null) return;

        var saved = _receiver.Progress.Where(file => file.Completed).Select(file => file.File.Name).ToArray();
        if (saved.Length == 0) return;

        _completionSent = true;
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(new TransferCompletion(IncomingManifest.SessionId, saved).ToJson());
            await _channel.SendAsync(TransferFrames.Encode(TransferFrameKind.Ack, -1, -1, payload, _groupKey));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The sender will report the missing confirmation; the file is still on disk here.
            _completionSent = false;
        }
    }

    private static async Task<TransferCompletion?> WaitForCompletionAsync(TaskCompletionSource<TransferCompletion> wait,
        TimeSpan timeout)
    {
        var completed = await Task.WhenAny(wait.Task, Task.Delay(timeout));
        return completed == wait.Task ? wait.Task.Result : null;
    }

    private async Task SendCancelAsync()
    {
        try
        {
            if (_channel.IsOpen)
                await _channel.SendAsync(TransferFrames.Encode(TransferFrameKind.Cancel, -1, -1, [], _groupKey));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The peer may already be gone; cancelling is best effort by nature.
        }
    }

    private void Refuse(string message, bool retryable)
    {
        LastError = message;
        Refused?.Invoke(new TransferRefusal(message, retryable));
    }

    public async ValueTask DisposeAsync()
    {
        _receiver.Dispose();
        _inbound.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_groupKey);
        await _channel.DisposeAsync();
    }
}
