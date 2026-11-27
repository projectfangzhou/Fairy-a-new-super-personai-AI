using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MyAiAssistant.Services.Messaging;

/// <summary>Outcome of a loopback round trip against the web chat endpoint.</summary>
public sealed record WebChatSelfTest(bool Ok, string Message, double RoundTripMs);

/// <summary>
/// Loopback WebSocket endpoint that proves the local messaging path end to end.
///
/// It speaks the RFC 6455 handshake and echoes one text frame per connection. It is not a chat
/// server and does not pretend to be one: what it has to answer is "can this machine host a
/// socket and can a browser actually talk to it", and a probe that merely opened a port would not
/// answer the second half.
///
/// Raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>, for the reason documented
/// on <c>LocalHttpServer</c>: HTTP.sys fails with "invalid handle" in some Windows sessions, and a
/// surface that cannot open is indistinguishable from a broken feature.
/// </summary>
public sealed class WebChatEndpoint : IDisposable
{
    public const int DefaultPort = 9881;

    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxPayloadBytes = 4 * 1024;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; } = DefaultPort;

    public bool IsRunning => _listener is not null;

    public string Url => $"ws://127.0.0.1:{Port}/";

    /// <summary>Binds loopback and starts accepting. Port 0 asks the OS for a free port.</summary>
    public void Start(int port = DefaultPort)
    {
        if (_listener is not null) return;
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0-65535 之间。");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Connects to this endpoint as an ordinary WebSocket client and expects its own message
    /// echoed back, so readiness is reported only after a real handshake and a real frame round
    /// trip. <paramref name="message"/> must stay under 126 bytes.
    /// </summary>
    public async Task<WebChatSelfTest> SelfTestAsync(string message = "ping",
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return new(false, "服务未启动。", 0);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = "GET / HTTP/1.1\r\n" +
                          "Host: 127.0.0.1\r\n" +
                          "Upgrade: websocket\r\n" +
                          "Connection: Upgrade\r\n" +
                          $"Sec-WebSocket-Key: {key}\r\n" +
                          "Sec-WebSocket-Version: 13\r\n\r\n";
            await WriteTextAsync(stream, request).ConfigureAwait(false);

            var header = await ReadHeaderAsync(stream).ConfigureAwait(false);
            if (header is null)
                return new(false, "握手没有回应。", stopwatch.Elapsed.TotalMilliseconds);

            var statusLine = header.Split("\r\n")[0];
            if (!statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                return new(false, "握手被拒绝：" + statusLine, stopwatch.Elapsed.TotalMilliseconds);

            var expected = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketAcceptGuid)));
            if (!header.Contains(expected, StringComparison.Ordinal))
                return new(false, "Sec-WebSocket-Accept 校验失败。", stopwatch.Elapsed.TotalMilliseconds);

            await WriteMaskedFrameAsync(stream, message).ConfigureAwait(false);
            var echoed = await ReadTextFrameAsync(stream).ConfigureAwait(false);
            stopwatch.Stop();

            return echoed == message
                ? new(true, $"本机 WebSocket 通路正常（{Url}）。", stopwatch.Elapsed.TotalMilliseconds)
                : new(false, "回显内容与发送内容不一致。", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            stopwatch.Stop();
            return new(false, $"无法连接本机端口 {Port}（{ex.GetType().Name}）。",
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                var key = await ReadHandshakeKeyAsync(stream).ConfigureAwait(false);
                if (key is null)
                {
                    await WriteTextAsync(stream,
                        "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
                        .ConfigureAwait(false);
                    return;
                }

                var accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketAcceptGuid)));
                await WriteTextAsync(stream,
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n").ConfigureAwait(false);

                var payload = await ReadTextFrameAsync(stream).ConfigureAwait(false);
                if (payload is null) return;

                await WriteFrameAsync(stream, payload).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
            }
        }
    }

    private static async Task<string?> ReadHandshakeKeyAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length))
                .ConfigureAwait(false);
            if (read == 0) return null;
            length += read;

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;

            foreach (var line in text.Split("\r\n"))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) continue;
                if (!line[..colon].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line[(colon + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        return null;
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length))
                .ConfigureAwait(false);
            if (read == 0) return null;
            length += read;

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }

        return null;
    }

    private static async Task<string?> ReadTextFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header).ConfigureAwait(false)) return null;

        var opcode = header[0] & 0x0F;
        if (opcode == 0x8) return null;

        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            if (!await ReadExactAsync(stream, extended).ConfigureAwait(false)) return null;
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            return null;
        }

        if (length > MaxPayloadBytes) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask).ConfigureAwait(false)) return null;

        var payload = new byte[(int)length];
        if (length > 0 && !await ReadExactAsync(stream, payload).ConfigureAwait(false)) return null;

        if (masked)
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] target)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target.AsMemory(offset, target.Length - offset))
                .ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        byte[] frame;

        if (bytes.Length <= 125)
        {
            frame = new byte[bytes.Length + 2];
            frame[0] = 0x81;
            frame[1] = (byte)bytes.Length;
            bytes.CopyTo(frame, 2);
        }
        else
        {
            frame = new byte[bytes.Length + 4];
            frame[0] = 0x81;
            frame[1] = 126;
            frame[2] = (byte)(bytes.Length >> 8);
            frame[3] = (byte)(bytes.Length & 0xFF);
            bytes.CopyTo(frame, 4);
        }

        await stream.WriteAsync(frame).ConfigureAwait(false);
    }

    private static async Task WriteMaskedFrameAsync(NetworkStream stream, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > 125)
            throw new ArgumentException("自检报文必须短于 126 字节。", nameof(payload));

        var mask = RandomNumberGenerator.GetBytes(4);
        var frame = new byte[bytes.Length + 6];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | bytes.Length);
        mask.CopyTo(frame, 2);

        for (var i = 0; i < bytes.Length; i++) frame[6 + i] = (byte)(bytes[i] ^ mask[i % 4]);

        await stream.WriteAsync(frame).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }
}
