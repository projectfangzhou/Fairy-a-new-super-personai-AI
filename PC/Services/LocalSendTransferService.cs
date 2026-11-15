using System.IO;
using MyAiAssistant.Services.LocalSend;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Relay;

namespace MyAiAssistant.Services;


/// <summary>
/// Runs discovery and sending for the LAN feature. Sending files out of this machine is an
/// external transfer, so every send is recorded in the operation audit; the peer is addressed
/// by the fingerprint it announced, never by a hand-typed URL.
/// </summary>
public sealed class LocalSendTransferService : ILocalSendPeers, IDisposable
{
    private readonly OperationAudit _audit;
    private readonly Action<string>? _log;
    private ILocalSendTransport? _transport;
    private LocalSendDiscovery? _discovery;
    private LocalSendDevice? _self;

    public LocalSendTransferService(OperationAudit audit, Action<string>? log = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log;
    }

    public bool DiscoveryRunning => _discovery is not null;

    public void Start(LocalSendReceiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Stop();

        var self = new LocalSendDevice(
            options.Alias, LocalSendProtocol.Version, options.DeviceModel,
            LocalSendProtocol.NormalizeDeviceType(options.DeviceType),
            options.Fingerprint, options.Port, "http", false);

        var transport = new UdpLocalSendTransport();
        var discovery = new LocalSendDiscovery(transport, self);
        try
        {
            discovery.Start(options.Port);
            _self = self;
            _transport = transport;
            _discovery = discovery;
            _log?.Invoke($"LocalSend 发现已启动：组播 {LocalSendProtocol.MulticastGroup}:{options.Port}");
        }
        catch (Exception ex)
        {
            // Discovery is optional: receiving still works when multicast cannot join (some
            // networks block it), so this must not take the receiver down with it.
            _log?.Invoke($"LocalSend 发现未能启动：{ex.Message}");
            discovery.Dispose();
        }
    }

    public void Stop()
    {
        var discovery = _discovery;
        _discovery = null;
        _transport = null;
        if (discovery is null) return;
        try { discovery.Dispose(); } catch (Exception) { }
    }

    public IReadOnlyList<LocalSendPeer> Peers()
    {
        var discovery = _discovery;
        if (discovery is null) return [];
        return discovery.Peers()
            .Select(peer => new LocalSendPeer(peer.Fingerprint, peer.Alias, peer.DeviceType, peer.Endpoint, peer.LastSeen))
            .ToArray();
    }

    public async Task<LocalSendSendResult> SendAsync(
        string fingerprint, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return new LocalSendSendResult(false, "缺少目标设备");
        if (paths.Count == 0)
            return new LocalSendSendResult(false, "没有选择文件");

        var discovery = _discovery;
        var self = _self;
        if (discovery is null || self is null)
            return new LocalSendSendResult(false, "设备发现未启动，无法发送");

        var peerDevice = discovery.Peers().FirstOrDefault(peer =>
            string.Equals(peer.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (peerDevice is null)
            return new LocalSendSendResult(false, "目标设备已不在线（对方停止广播）");

        var missing = paths.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            return new LocalSendSendResult(false, $"找不到文件：{string.Join("、", missing.Take(3))}");

        var quota = RelayQuotaBinding.FromConfiguration();
        var sizes = paths.Select(path => new FileInfo(path).Length).ToArray();
        var decision = quota.Evaluate(sizes);
        if (!decision.Allowed)
        {
            _log?.Invoke($"LocalSend 发送被额度拒绝：{decision.Reason}");
            return new LocalSendSendResult(false, decision.Reason);
        }

        using var client = new LocalSendClient();
        var result = await client.SendFilesAsync(peerDevice, self, paths, cancellationToken).ConfigureAwait(false);

        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "localsend.send",
            RiskCategory.ExternalTransfer,
            $"向 {peerDevice.Alias} 发送 {paths.Count} 个文件",
            peerDevice.Endpoint,
            result.Ok ? "allowed" : "failed",
            result.Message,
            "user",
            0,
            paths.Select(Path.GetFileName).OfType<string>().Take(8).ToArray()));

        return new LocalSendSendResult(result.Ok, result.Message, result.UploadedFiles);
    }

    /// <summary>What discovery believes this machine is; used by the UI to show the announced name.</summary>
    public LocalSendDevice? Self => _self;

    public void Dispose() => Stop();
}
