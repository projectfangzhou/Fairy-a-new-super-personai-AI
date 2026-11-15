using System.Diagnostics;
using System.Text;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>
/// The socket seam. Tests drive discovery logic without touching multicast, which is exactly
/// the part that behaves differently on every network.
/// </summary>
public interface ILocalSendTransport : IDisposable
{
    void Start(int port);

    Task SendAsync(byte[] payload, CancellationToken cancellationToken);

    /// <summary>Waits for the next announce; null when the wait was cancelled.</summary>
    Task<(byte[] Data, string? SourceAddress)?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Announces this device to the LocalSend multicast group and keeps a live table of peers.
///
/// Peers are only shown while they are still announcing: a stale entry that no longer answers
/// is worse than no entry, because the user would try to send to a device that is gone.
/// </summary>
public sealed class LocalSendDiscovery : IDisposable
{
    private readonly ILocalSendTransport _transport;
    private readonly object _sync = new();
    private readonly Dictionary<string, LocalSendDevice> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _announceInterval;
    private readonly TimeSpan _expiry;
    private Task? _loop;
    private int _announceCount;
    private int _ignoredCount;
    private LocalSendDevice _self;

    public LocalSendDiscovery(
        ILocalSendTransport transport,
        LocalSendDevice self,
        TimeSpan? announceInterval = null,
        TimeSpan? expiry = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _self = self ?? throw new ArgumentNullException(nameof(self));
        _announceInterval = announceInterval ?? TimeSpan.FromSeconds(5);
        _expiry = expiry ?? TimeSpan.FromSeconds(60);
    }

    public LocalSendDevice Self => _self;

    public int AnnouncementsSent => Volatile.Read(ref _announceCount);

    public int IgnoredAnnouncements => Volatile.Read(ref _ignoredCount);

    public IReadOnlyList<LocalSendDevice> Peers()
    {
        var cutoff = DateTimeOffset.UtcNow - _expiry;
        lock (_sync)
        {
            foreach (var stale in _peers.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key).ToArray())
                _peers.Remove(stale);
            return _peers.Values.OrderBy(peer => peer.Alias, StringComparer.CurrentCulture).ToArray();
        }
    }

    public void Start(int port = LocalSendProtocol.DefaultPort)
    {
        _transport.Start(port);
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Sends one announce. Exposed so the UI can offer "refresh now".</summary>
    public async Task AnnounceOnceAsync(CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes(LocalSendProtocol.BuildAnnounce(_self));
        await _transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _announceCount);
    }

    /// <summary>Applies one received announce. Returns the peer it produced, or null if ignored.</summary>
    public LocalSendDevice? Observe(byte[] data, string? sourceAddress)
    {
        LocalSendDevice? parsed;
        try
        {
            parsed = LocalSendProtocol.ParseAnnounce(Encoding.UTF8.GetString(data), sourceAddress);
        }
        catch (Exception)
        {
            parsed = null;
        }

        // Our own datagram comes back on loopback; it is not a peer.
        if (parsed is null || string.Equals(parsed.Fingerprint, _self.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _ignoredCount);
            return null;
        }

        parsed = parsed with { LastSeen = DateTimeOffset.UtcNow, Address = sourceAddress ?? parsed.Address };
        lock (_sync) _peers[parsed.Fingerprint] = parsed;
        return parsed;
    }

    /// <summary>Used when the user edits the alias or port in settings.</summary>
    public void UpdateSelf(LocalSendDevice self)
    {
        _self = self ?? throw new ArgumentNullException(nameof(self));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var receive = Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await AnnounceOnceAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"localsend announce failed: {ex.Message}"); }

                try { await Task.Delay(_announceInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { await receive.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            (byte[] Data, string? SourceAddress)? datagram;
            try { datagram = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine($"localsend receive failed: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (datagram is null) return;
            Observe(datagram.Value.Data, datagram.Value.SourceAddress);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _stop.Dispose(); } catch (Exception) { }
        try { _transport.Dispose(); } catch (Exception) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
