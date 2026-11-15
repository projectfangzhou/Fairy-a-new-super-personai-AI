using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MyAiAssistant.Services.Http;

public sealed record HttpRequestData(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    bool Chunked,
    string RemoteAddress = "");

public sealed record HttpResponseData(
    int Status,
    string Body,
    string ContentType = "application/json; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Minimal HTTP/1.1 server used for the local control surfaces.
///
/// It deliberately does not use <see cref="HttpListener"/>: that goes through HTTP.sys, which
/// fails with "invalid handle" in some Windows sessions. A control surface that cannot open is
/// indistinguishable from a broken feature, so the socket is handled directly.
///
/// The bind address is explicit and defaults to loopback. Only the surfaces that have to be
/// reachable from another device — pairing — ask for a wider address, and those have to
/// authenticate every request on their own because nothing else does it for them.
/// Each connection serves exactly one request and is closed.
/// </summary>
public sealed class LocalHttpServer
{
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly Func<HttpRequestData, Task<HttpResponseData>> _handler;
    private readonly int _maxBodyBytes;
    private TcpListener? _listener;
    private int _port;
    private IPAddress _address = IPAddress.Loopback;

    public LocalHttpServer(Func<HttpRequestData, Task<HttpResponseData>> handler, int maxBodyBytes = 1024 * 1024)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (maxBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        _maxBodyBytes = maxBodyBytes;
    }

    public bool IsRunning => _listener is not null;

    public int Port => _port;

    /// <summary>The bound address, so callers can tell a loopback surface from a reachable one.</summary>
    public IPAddress BoundAddress => _address;

    public bool IsReachableFromNetwork => !IPAddress.IsLoopback(_address) && !Equals(_address, IPAddress.None);

    public string? Url => _listener is null ? null : $"http://{_address}:{_port}/";

    public string? LastError { get; private set; }

    /// <summary>Binds the requested port, or any free port when it is 0 or already taken.</summary>
    public bool Start(int port, IPAddress? address = null)
    {
        if (_listener is not null) return true;

        var target = address ?? IPAddress.Loopback;
        if (TryBind(target, port))
            return true;

        return port != 0 && target.Equals(IPAddress.Loopback) && TryBind(target, 0);
    }

    private bool TryBind(IPAddress address, int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            listener.Start();
            _listener = listener;
            _address = address;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptAsync(listener));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            try { listener?.Stop(); } catch (Exception) { /* nothing bound */ }
            return false;
        }
    }

    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        try { listener?.Stop(); } catch (Exception) { /* already closed */ }
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (_listener == listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception) { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    {
                        client.ReceiveTimeout = 15_000;
                        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
                        using var stream = client.GetStream();
                        var request = await ReadRequestAsync(stream, remote).ConfigureAwait(false);
                        var response = await _handler(request).ConfigureAwait(false);
                        await WriteResponseAsync(stream, response).ConfigureAwait(false);
                    }
                }
                catch (Exception) { /* a client hanging up must not stop the server */ }
            });
        }
    }

    private async Task<HttpRequestData> ReadRequestAsync(NetworkStream stream, string remoteAddress)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        var headerEnd = -1;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            headerEnd = IndexOfHeaderEnd(buffer, total);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0)
            throw new InvalidDataException("请求头不完整");

        var head = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("请求行为空");

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) throw new InvalidDataException("请求行不合法");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
            int.TryParse(lengthText, out contentLength);
        if (contentLength is < 0 || contentLength > _maxBodyBytes)
            throw new InvalidDataException("请求体过大");

        var bodyStart = headerEnd + 4;
        var bodyBytes = new List<byte>(contentLength);
        var already = Math.Max(0, total - bodyStart);
        for (var i = 0; i < Math.Min(already, contentLength); i++)
            bodyBytes.Add(buffer[bodyStart + i]);

        while (bodyBytes.Count < contentLength)
        {
            var chunk = new byte[Math.Min(8192, contentLength - bodyBytes.Count)];
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) break;
            bodyBytes.AddRange(chunk.Take(read));
        }

        return new HttpRequestData(
            parts[0].ToUpperInvariant(),
            parts[1],
            headers,
            Encoding.UTF8.GetString(bodyBytes.ToArray()),
            headers.TryGetValue("Transfer-Encoding", out var encoding) &&
            encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase),
            remoteAddress);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponseData response)
    {
        var payload = Encoding.UTF8.GetBytes(response.Body);
        var reason = response.Status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            411 => "Length Required",
            413 => "Payload Too Large",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Error"
        };

        var head = new StringBuilder()
            .Append($"HTTP/1.1 {response.Status} {reason}\r\n")
            .Append($"Content-Type: {response.ContentType}\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");

        if (response.Headers is not null)
            foreach (var header in response.Headers)
                head.Append($"{header.Key}: {header.Value}\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString())).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
