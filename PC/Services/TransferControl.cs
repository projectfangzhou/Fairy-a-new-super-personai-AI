using System.IO;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Transfer;

namespace MyAiAssistant.Services;

/// <summary>
/// Runs file transfers between this machine and the devices it has paired with.
///
/// This machine owns the relay, so it is the one that polls the mailbox and routes signals: an
/// inbound offer creates an answering session, and every session's outbound signal goes straight
/// into the peer's inbox. Nothing is received without the user answering the approval that lands in
/// the same pending list every other risky action uses.
/// </summary>
public sealed class TransferControl : ITransferControl, IDisposable
{
    private const int MaxJobs = 20;
    private const int MaxSendAttempts = 3;
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(45);

    private readonly PairingService _pairing;
    private readonly PairingEndpointServer _endpoint;
    private readonly AgentControlCenter _center;
    private readonly Func<CancellationToken, Task<ITransferChannel>> _channelFactory;
    private readonly Func<TransferOffer, CancellationToken, Task<bool>> _approval;
    private readonly Func<bool> _isEnabled;
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _since = new(StringComparer.Ordinal);
    private readonly List<TransferJob> _jobs = [];
    private readonly List<TransferFileProgress> _received = [];

    private CancellationTokenSource? _loop;
    private int _jobCounter;

    public TransferControl(PairingService pairing, PairingEndpointServer endpoint, AgentControlCenter center,
        Func<CancellationToken, Task<ITransferChannel>> channelFactory, string? destinationRoot = null,
        Func<TransferOffer, CancellationToken, Task<bool>>? approval = null, Func<bool>? isEnabled = null)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _approval = approval ?? AskThroughPendingApprovalAsync;
        _isEnabled = isEnabled ?? (() => ConfigManager.Load().Pairing.Enabled);

        DestinationRoot = string.IsNullOrWhiteSpace(destinationRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "received")
            : Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(DestinationRoot);
    }

    public string DestinationRoot { get; }

    private string HostDeviceId => _pairing.Store.Identity.DeviceId;

    public TransferView Read()
    {
        var peers = new List<TransferPeer>();
        foreach (var group in _pairing.Store.Groups)
        {
            foreach (var member in group.Members)
                peers.Add(new TransferPeer(member.DeviceId, member.DisplayName, group.GroupId, group.Name));
        }

        lock (_gate) return new TransferView(_isEnabled(), LastError, HostDeviceId, DestinationRoot,
            peers, _jobs.ToArray(),
            _received.Select(file => new ReceivedFileView(file.File.Name, file.File.Size, file.Completed, file.SavedPath)).ToArray());
    }

    public string? LastError { get; private set; }

    /// <summary>Starts routing signals. Only runs while pairing is enabled, since there is no relay otherwise.</summary>
    public bool Start()
    {
        if (!_isEnabled()) return false;
        if (_loop is not null) return true;

        _loop = new CancellationTokenSource();
        _ = Task.Run(() => PollAsync(_loop.Token));
        return true;
    }

    public void Stop()
    {
        var loop = _loop;
        _loop = null;
        try { loop?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        loop?.Dispose();
    }

    public async Task<TransferCommandResult> SendAsync(string deviceId, IReadOnlyList<string> paths)
    {
        if (!_isEnabled()) return new TransferCommandResult(false, "配对未开启，无法建立传输通道");
        if (string.IsNullOrWhiteSpace(deviceId)) return new TransferCommandResult(false, "没有指定接收设备");
        if (paths is null || paths.Count == 0) return new TransferCommandResult(false, "没有要发送的文件");

        var missing = paths.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            return new TransferCommandResult(false, $"这些文件不存在：{string.Join("、", missing.Select(Path.GetFileName))}");

        var group = _pairing.Store.Groups.FirstOrDefault(g => g.Members.Any(m => m.DeviceId == deviceId));
        if (group is null) return new TransferCommandResult(false, "这台设备不在任何已配对的设备组里");

        var job = AddJob(deviceId, "发送", "连接中", 0, null);

        // The id of this transfer, not of this attempt: every retry passes the same one, so the
        // receiver answers with what it already holds instead of taking the whole file again.
        var sessionId = Guid.NewGuid().ToString("N")[..16];

        for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
        {
            Link? link = null;
            try
            {
                if (attempt == 1) Update(job, "连接中", 0, null);
                else Update(job, $"重试中（第 {attempt} 次）", 0, null);

                link = await CreateLinkAsync(group.GroupId, deviceId);
                if (link is null) return Update(job, "失败", 0, LastError ?? "通道创建失败");

                if (!await link.Session.StartChannelAsync(WebRtcRole.Offerer) ||
                    !await link.Session.WaitForChannelAsync(ChannelTimeout))
                {
                    RemoveLink(link);
                    if (attempt == MaxSendAttempts)
                        return Update(job, "失败", 0, "连接没有建立起来（对方可能不在线，或信令没有送达）");

                    await Task.Delay(Backoff(attempt));
                    continue;
                }

                Update(job, "传输中", 0, null);
                var bytes = await link.Session.SendFilesAsync(paths, _pairing.HostDisplayName,
                    CancellationToken.None, sessionId);
                RemoveLink(link);
                Update(job, "已完成", bytes, null);
                return new TransferCommandResult(true, $"已发送 {paths.Count} 个文件");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                if (link is not null) RemoveLink(link);
                if (attempt == MaxSendAttempts) return Update(job, "失败", 0, ex.Message);

                await Task.Delay(Backoff(attempt));
            }
        }

        return Update(job, "失败", 0, "多次重试仍未成功");
    }

    /// <summary>Doubling wait between attempts, short enough for a LAN and capped by the attempt count.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(8, 1 << (attempt - 1)));

    private async Task<Link?> CreateLinkAsync(string groupId, string peerDeviceId)
    {
        var groupKey = _pairing.Store.TryGetGroupKey(groupId);
        if (groupKey is null)
        {
            LastError = "设备组密钥不可用";
            return null;
        }

        ITransferChannel channel;
        try
        {
            channel = await _channelFactory(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            LastError = ex.Message;
            Array.Clear(groupKey);
            return null;
        }

        // The pending approval is the app's own timeout-bearing surface: leaving it open is the
        // intended state until the user answers, exactly like any other risky action here.
        var session = new TransferSession(channel, groupKey, DestinationRoot,
            offer => _approval(offer, CancellationToken.None));
        Array.Clear(groupKey);

        var key = groupId + "|" + peerDeviceId;
        var link = new Link(key, groupId, peerDeviceId, session);

        // Outbound signalling goes straight into the peer's inbox: this machine is the relay.
        session.OutboundSignal += signal => _endpoint.EnqueueSignal(groupId, peerDeviceId, HostDeviceId, signal);
        session.FileReceived += progress => RecordReceived(peerDeviceId, progress);
        session.Refused += refusal => Record(peerDeviceId, "接收", "失败", 0, refusal.Message);

        lock (_gate) _links[key] = link;
        return link;
    }

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var group in _pairing.Store.Groups)
                {
                    var since = _since.TryGetValue(group.GroupId, out var value) ? value : 0L;
                    var signals = _endpoint.TakeSignals(group.GroupId, HostDeviceId, ref since);
                    _since[group.GroupId] = since;

                    foreach (var received in signals)
                        await RouteAsync(group.GroupId, received);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                LastError = ex.Message;
            }

            try { await Task.Delay(400, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RouteAsync(string groupId, ReceivedSignal received)
    {
        var key = groupId + "|" + received.From;

        Link? existing;
        lock (_gate) _links.TryGetValue(key, out existing);

        if (existing is not null)
        {
            await existing.Session.DeliverSignalAsync(received.Signal);
            return;
        }

        // Only an offer can start a session; anything else has nothing to attach to.
        if (received.Signal.Kind != TransferSignalKind.Offer) return;

        var job = AddJob(received.From, "接收", "连接中", 0, null);
        var link = await CreateLinkAsync(groupId, received.From);
        if (link is null)
        {
            Update(job, "失败", 0, LastError ?? "通道创建失败");
            return;
        }

        await link.Session.StartChannelAsync(WebRtcRole.Answerer);
        await link.Session.DeliverSignalAsync(received.Signal);
        Update(job, "等待确认", 0, null);
    }

    private void RecordReceived(string peerDeviceId, TransferFileProgress progress)
    {
        lock (_gate) _received.Add(progress);

        var name = progress.File.Name;
        Record(peerDeviceId, "接收", $"已保存 {name}", progress.File.Size, null);
    }

    private void Record(string peer, string direction, string state, long bytes, string? error)
    {
        lock (_gate)
        {
            var index = _jobs.FindIndex(job => job.Peer == peer && job.Direction == direction && job.State is "连接中" or "传输中" or "等待确认");
            if (index >= 0)
            {
                _jobs[index] = _jobs[index] with { State = state, Bytes = bytes, Error = error };
                return;
            }

            _jobs.Insert(0, new TransferJob(NextJobId(), peer, direction, state, bytes, error, DateTimeOffset.UtcNow));
            Trim();
        }
    }

    /// <summary>The approval every risky action goes through: a pending item in the shared list.</summary>
    private async Task<bool> AskThroughPendingApprovalAsync(TransferOffer offer, CancellationToken cancellationToken)
    {
        var task = _center.Enqueue($"接收文件：{offer.Manifest.Files.Count} 个");
        task.Start();

        var files = string.Join("\n", offer.Manifest.Files.Take(10).Select(file => $"· {file.Name}"));
        var more = offer.Manifest.Files.Count > 10 ? $"\n（另有 {offer.Manifest.Files.Count - 10} 个）" : string.Empty;

        try
        {
            var approved = await task.AskApprovalAsync($"{offer.Describe()}\n{files}{more}",
                RiskCategory.ExternalTransfer, cancellationToken).ConfigureAwait(false);

            if (approved) task.Complete();
            else task.Fail("用户拒绝接收");
            return approved;
        }
        catch (OperationCanceledException)
        {
            task.Fail("审批超时或已取消");
            return false;
        }
    }

    private TransferJob AddJob(string peer, string direction, string state, long bytes, string? error)
    {
        lock (_gate)
        {
            var job = new TransferJob(NextJobId(), peer, direction, state, bytes, error, DateTimeOffset.UtcNow);
            _jobs.Insert(0, job);
            Trim();
            return job;
        }
    }

    private TransferCommandResult Update(TransferJob job, string state, long bytes, string? error)
    {
        lock (_gate)
        {
            var index = _jobs.FindIndex(entry => entry.Id == job.Id);
            if (index >= 0) _jobs[index] = _jobs[index] with { State = state, Bytes = bytes, Error = error };
        }

        return new TransferCommandResult(state == "已完成" || state.StartsWith("已保存", StringComparison.Ordinal),
            error ?? state);
    }

    private string NextJobId() => "t" + Interlocked.Increment(ref _jobCounter).ToString("D4");

    private void Trim()
    {
        if (_jobs.Count > MaxJobs) _jobs.RemoveRange(MaxJobs, _jobs.Count - MaxJobs);
        if (_received.Count > MaxJobs) _received.RemoveRange(0, _received.Count - MaxJobs);
    }

    private void RemoveLink(Link link)
    {
        lock (_gate) _links.Remove(link.Key);
        _ = link.Session.DisposeAsync().AsTask();
    }

    public void Dispose()
    {
        Stop();

        Link[] links;
        lock (_gate)
        {
            links = _links.Values.ToArray();
            _links.Clear();
        }

        foreach (var link in links) link.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record Link(string Key, string GroupId, string PeerDeviceId, TransferSession Session);
}
