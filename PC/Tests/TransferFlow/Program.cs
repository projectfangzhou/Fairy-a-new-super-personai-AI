using System.IO;
using System.Net;
using System.Security.Cryptography;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Transfer;

// Two devices, end to end, with only the data channel itself simulated.
//
// The signalling really goes over the pairing endpoint (HTTP, sealed with the group key), the
// approval really goes through the shared pending queue, and a retry really resumes from progress
// kept on disk. The one substitution is the WebRTC data channel, which Tests/WebRtc covers against
// a real one. This is therefore where the wiring between the pieces is exercised, not the transport.

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

async Task WaitUntil(Func<bool> condition, string name, int timeoutMs = 15000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition())
        {
            checks++;
            return;
        }

        await Task.Delay(50);
    }

    throw new Exception("TIMEOUT: " + name);
}

var workspace = Path.Combine(AppContext.BaseDirectory, "flow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    var protector = new XorProtector();
    var hostDestination = Path.Combine(workspace, "host-received");
    var memberDestination = Path.Combine(workspace, "member-received");
    Directory.CreateDirectory(hostDestination);
    Directory.CreateDirectory(memberDestination);

    // ---- the host: a group, a relay, and the control surface that drives transfers ----
    using var pairing = new PairingService(
        new DeviceGroupStore(Path.Combine(workspace, "host"), protector),
        new PairingInviteStore());
    var (groupCreated, _, group) = pairing.Store.CreateGroup("联调");
    Assert(groupCreated && group is not null, "the host has a group");

    var groupId = group!.GroupId;
    var hostId = pairing.Store.Identity.DeviceId;
    var groupKey = pairing.Store.TryGetGroupKey(groupId)!;

    using var memberIdentity = DeviceIdentity.Create();
    var (memberAdded, _, _) = pairing.Store.AddMember(groupId, new PairingRequest(
        "INVSEED", groupId, memberIdentity.DeviceId, "平板", memberIdentity.SigningPublicKey,
        memberIdentity.AgreementPublicKey, memberIdentity.AgreementPublicKey,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
    Assert(memberAdded, "the member is in the group");

    var endpoint = new PairingEndpointServer(pairing, port: 0);
    Assert(endpoint.Start(IPAddress.Loopback), "the relay starts");

    var center = new AgentControlCenter();
    var hostChannels = new List<SignalledChannel>();

    var hostControl = new TransferControl(pairing, endpoint, center,
        _ =>
        {
            var channel = new SignalledChannel();
            lock (hostChannels) hostChannels.Add(channel);
            return Task.FromResult<ITransferChannel>(channel);
        },
        hostDestination,
        isEnabled: () => true);

    hostControl.Start();
    Assert(hostControl.Read().Peers.Count == 1, "the host offers the paired device as a target");

    // ---- the member: real signalling over the relay, same session logic ----
    using var signals = new SignalClient($"http://127.0.0.1:{endpoint.Port}", groupId,
        memberIdentity.DeviceId, hostId, groupKey);

    var memberChannel = new SignalledChannel();
    memberChannel.SignalOutbound += signal => _ = signals.SendAsync(hostId, signal);

    TransferOffer? memberOffer = null;
    await using var memberReceiver = new TransferSession(memberChannel, groupKey, memberDestination, offer =>
    {
        memberOffer = offer;
        return Task.FromResult(true);
    });

    var memberReceived = new TaskCompletionSource<TransferFileProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
    memberReceiver.FileReceived += progress => memberReceived.TrySetResult(progress);

    // One signalling path per device, demultiplexed by kind: an offer belongs to the side that will
    // answer, an answer or a candidate to the side that offered. Two independent pollers on the same
    // client would race for messages and lose one.
    var answerers = new List<TransferSession> { memberReceiver };
    var offerers = new List<TransferSession>();

    using var memberCancellation = new CancellationTokenSource();
    var memberPump = Task.Run(async () =>
    {
        while (!memberCancellation.IsCancellationRequested)
        {
            var poll = await signals.PollAsync(memberCancellation.Token);
            foreach (var received in poll.Signals)
            {
                var targets = received.Signal.Kind == TransferSignalKind.Offer ? answerers : offerers;
                foreach (var session in targets.ToArray()) await session.DeliverSignalAsync(received.Signal);
            }

            try { await Task.Delay(60, memberCancellation.Token); }
            catch (OperationCanceledException) { return; }
        }
    });

    // ---- host → member ----
    var outgoing = Path.Combine(workspace, "发给平板.bin");
    var outgoingBytes = RandomNumberGenerator.GetBytes(150 * 1024 + 11);
    File.WriteAllBytes(outgoing, outgoingBytes);

    var sendTask = hostControl.SendAsync(memberIdentity.DeviceId, [outgoing]);

    await WaitUntil(() => { lock (hostChannels) return hostChannels.Count == 1; },
        "the host opens a channel for the send");

    // Only the data path is simulated: the two halves are joined here instead of by WebRTC.
    SignalledChannel hostSendChannel;
    lock (hostChannels) hostSendChannel = hostChannels[0];
    hostSendChannel.Peer = memberChannel;
    memberChannel.Peer = hostSendChannel;

    var sent = await sendTask;
    Assert(sent.Ok, $"host to member completes: {sent.Message}");

    var receivedInTime = await Task.WhenAny(memberReceived.Task, Task.Delay(TimeSpan.FromSeconds(15))) == memberReceived.Task;
    if (!receivedInTime)
    {
        Console.WriteLine("MEMBER STATE: " + string.Join(" | ",
            memberReceiver.Progress.Select(file => $"{file.File.Name} {file.ReceivedChunks}/{file.ChunkCount} done={file.Completed}")));
        Console.WriteLine("MEMBER LAST ERROR: " + (memberReceiver.LastError ?? "(none)"));
        Console.WriteLine("HOST JOB: " + hostControl.Read().Jobs.First().State + " / " + hostControl.Read().Jobs.First().Error);
    }

    Assert(receivedInTime, "the member reports the file as received");
    Assert(File.ReadAllBytes(Path.Combine(memberDestination, "发给平板.bin")).SequenceEqual(outgoingBytes),
        "the member's copy is byte-for-byte identical");
    Assert(memberOffer is not null && memberOffer.Manifest.Sender.Length > 0,
        "the member was asked before anything was written, and knows who is sending");
    Assert(hostControl.Read().Jobs.First().State == "已完成",
        "the host's job ends completed: " + hostControl.Read().Jobs.First().State);

    // ---- member → host: the approval in the shared queue is what lets the write happen ----
    var incoming = Path.Combine(workspace, "来自平板.bin");
    var incomingBytes = RandomNumberGenerator.GetBytes(64 * 1024 + 5);
    File.WriteAllBytes(incoming, incomingBytes);

    var memberSender = new SignalledChannel();
    memberSender.SignalOutbound += signal => _ = signals.SendAsync(hostId, signal);
    await using var memberSendSession = new TransferSession(memberSender, groupKey, memberDestination,
        _ => Task.FromResult(true));

    offerers.Add(memberSendSession);
    Assert(await memberSendSession.StartChannelAsync(WebRtcRole.Offerer), "the member starts offering");

    await WaitUntil(() => { lock (hostChannels) return hostChannels.Count == 2; },
        "the host answers the inbound offer");
    SignalledChannel hostReceiveChannel;
    lock (hostChannels) hostReceiveChannel = hostChannels[1];
    hostReceiveChannel.Peer = memberSender;
    memberSender.Peer = hostReceiveChannel;

    // The sender waits for the acceptance before pushing bytes, so the send is started first and
    // finished only after the person has answered.
    await WaitUntil(() => memberSender.IsOpen, "the member's channel opens once the host answers");
    var memberSendTask = memberSendSession.SendFilesAsync([incoming], "平板");

    await WaitUntil(() => center.Pending().Count > 0, "the incoming transfer asks for approval");
    Assert(!File.Exists(Path.Combine(hostDestination, "来自平板.bin")),
        "nothing is written while the approval is open");
    Assert(center.Pending().First().Risk == RiskCategory.ExternalTransfer,
        "the approval is filed under external transfer");

    foreach (var pending in center.Pending().ToArray()) center.Answer(pending.Id, true);
    await memberSendTask;

    await WaitUntil(() => File.Exists(Path.Combine(hostDestination, "来自平板.bin")),
        "the approved transfer writes the file");
    Assert(File.ReadAllBytes(Path.Combine(hostDestination, "来自平板.bin")).SequenceEqual(incomingBytes),
        "the host's copy is byte-for-byte identical");

    // ---- a refused approval writes nothing, and the sender is told why ----
    var refusedPath = Path.Combine(workspace, "被拒绝.bin");
    File.WriteAllBytes(refusedPath, RandomNumberGenerator.GetBytes(8 * 1024));

    var refusal = new TaskCompletionSource<TransferRefusal>(TaskCreationOptions.RunContinuationsAsynchronously);
    memberSendSession.Refused += value => refusal.TrySetResult(value);

    var refusedSend = memberSendSession.SendFilesAsync([refusedPath], "平板");
    await WaitUntil(() => center.Pending().Count > 0, "a second transfer asks for approval again");
    foreach (var pending in center.Pending().ToArray()) center.Answer(pending.Id, false);

    try
    {
        await refusedSend;
        Assert(false, "a refused transfer should not report success");
    }
    catch (InvalidOperationException)
    {
        Assert(true, "the refused send reports the refusal instead of pretending it worked");
    }

    Assert(await Task.WhenAny(refusal.Task, Task.Delay(TimeSpan.FromSeconds(10))) == refusal.Task,
        "the sending side learns the transfer was refused");
    Assert(!refusal.Task.Result.Retryable, "a refusal is final, so it is not retried automatically");
    Assert(!File.Exists(Path.Combine(hostDestination, "被拒绝.bin")), "a refused transfer leaves no file");

    // ---- member → host again, this time continuing a transfer that is already half here ----
    // The host's earlier refusal left no progress, so this is a fresh transfer that must still work
    // with a channel that had already carried bytes: a second file on the same link.
    var second = Path.Combine(workspace, "第二个.bin");
    var secondBytes = RandomNumberGenerator.GetBytes(24 * 1024);
    File.WriteAllBytes(second, secondBytes);

    var secondSend = memberSendSession.SendFilesAsync([second], "平板");
    await WaitUntil(() => center.Pending().Count > 0, "the third transfer asks for approval");
    foreach (var pending in center.Pending().ToArray()) center.Answer(pending.Id, true);
    await secondSend;
    await WaitUntil(() => File.Exists(Path.Combine(hostDestination, "第二个.bin")),
        "the same link carries a second file");
    Assert(File.ReadAllBytes(Path.Combine(hostDestination, "第二个.bin")).SequenceEqual(secondBytes),
        "the second file is intact too");

    memberCancellation.Cancel();
    await Task.WhenAny(memberPump, Task.Delay(1000));
    hostControl.Dispose();
    endpoint.Stop();

    Console.WriteLine($"All {checks} transfer-flow checks passed.");
    return 0;
}
finally
{
    try { Directory.Delete(workspace, true); } catch (IOException) { }
}

/// <summary>
/// A data channel that only opens after the two sides have exchanged signalling, so the real
/// establishment path — offer, answer, both sides open — is exercised. Bytes then cross directly.
/// </summary>
internal sealed class SignalledChannel : ITransferChannel
{
    private readonly Queue<byte[]> _pending = new();
    private Action<byte[]>? _bytesReceived;

    public SignalledChannel? Peer { get; set; }

    public event Action<TransferSignal>? SignalOutbound;

    public event Action? ChannelOpened;

    /// <summary>
    /// Delivered only once the other side has a session attached. A real data channel buffers until
    /// the application reads it, so a harness that dropped those bytes would fail for a reason that
    /// cannot happen in the real transport.
    /// </summary>
    public event Action<byte[]>? BytesReceived
    {
        add
        {
            _bytesReceived += value;
            FlushPending();
        }
        remove => _bytesReceived -= value;
    }

    public event Action<string>? Failed;

    public bool IsOpen { get; private set; }

    public Task StartAsync(WebRtcRole role)
    {
        if (role == WebRtcRole.Offerer)
            SignalOutbound?.Invoke(new TransferSignal(TransferSignalKind.Offer, "{\"sdp\":\"offer\"}"));

        return Task.CompletedTask;
    }

    public Task ApplySignalAsync(TransferSignal signal)
    {
        switch (signal.Kind)
        {
            case TransferSignalKind.Offer:
                Open();
                SignalOutbound?.Invoke(new TransferSignal(TransferSignalKind.Answer, "{\"sdp\":\"answer\"}"));
                break;
            case TransferSignalKind.Answer:
                Open();
                break;
            case TransferSignalKind.Ice:
                break;
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        if (!IsOpen) throw new InvalidOperationException("数据通道尚未建立");

        var peer = Peer;
        if (peer is null) return;

        // Delivered on another turn so the two sessions do not recurse into each other.
        await Task.Yield();
        peer.Deliver(data.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Failed?.Invoke("closed");
        return ValueTask.CompletedTask;
    }

    private void Deliver(byte[] data)
    {
        if (_bytesReceived is null)
        {
            _pending.Enqueue(data);
            return;
        }

        _bytesReceived(data);
    }

    private void FlushPending()
    {
        while (_pending.Count > 0 && _bytesReceived is not null)
            _bytesReceived(_pending.Dequeue());
    }

    private void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        ChannelOpened?.Invoke();
    }
}

internal sealed class XorProtector : IPairingSecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(Scramble(System.Text.Encoding.UTF8.GetBytes(plaintext)));

    public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Scramble(Convert.FromBase64String(ciphertext)));

    private static byte[] Scramble(byte[] input)
    {
        var output = new byte[input.Length];
        for (var index = 0; index < input.Length; index++) output[index] = (byte)(input[index] ^ 0x5A);
        return output;
    }
}
