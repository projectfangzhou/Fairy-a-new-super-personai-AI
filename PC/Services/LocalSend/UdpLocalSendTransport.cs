using System.IO;
using System.Net;
using System.Net.Sockets;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>
/// The real multicast socket behind <see cref="LocalSendDiscovery"/>.
///
/// It joins 224.0.0.167 on the LocalSend port, so the official app shows this machine in its
/// list, and sends our announce to the same group. Loopback of our own datagram is disabled at
/// the socket level; the discovery layer also ignores it, because a peer list that contains
/// itself is a bug the user will notice before anything else.
/// </summary>
public sealed class UdpLocalSendTransport : ILocalSendTransport
{
    private readonly IPAddress _group = IPAddress.Parse(LocalSendProtocol.MulticastGroup);
    private UdpClient? _client;
    private int _port = LocalSendProtocol.DefaultPort;

    public bool IsRunning => _client is not null;

    public string? LastError { get; private set; }

    public void Start(int port)
    {
        if (_client is not null) return;
        _port = port <= 0 ? LocalSendProtocol.DefaultPort : port;

        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            // ReuseAddress lets the receiver's TCP listener and other LocalSend instances share
            // the port; without it a second instance cannot start at all.
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.ExclusiveAddressUse = false;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            client.JoinMulticastGroup(_group);
            client.MulticastLoopback = false;
            _client = client;
            LastError = null;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            try { client.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("UDP 传输尚未启动");
        await client.SendAsync(payload, new IPEndPoint(_group, _port), cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Data, string? SourceAddress)?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null) return null;
        try
        {
            var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return (result.Buffer, result.RemoteEndPoint.Address.ToString());
        }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    public void Dispose()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try { client.DropMulticastGroup(_group); } catch (Exception) { }
        try { client.Dispose(); } catch (Exception) { }
    }
}
