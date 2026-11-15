using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Pairing;

namespace TransferSelfTest;

/// <summary>
/// Serves the phone page over plain HTTP.
///
/// No TLS on purpose: a certificate for a LAN address would mean a warning on the phone, and this
/// tool would rather not have a second failure mode in the way. The page therefore brings its own
/// AES-GCM and SHA-256 (``simple-crypto.js``), verified against Node's crypto by the tool itself.
/// </summary>
internal sealed class WebHost : IDisposable
{
    private readonly TcpListener _listener;

    private readonly PairingEndpointServer _relay;
    private readonly SessionInfo _session;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _pageOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebHost(PairingEndpointServer relay, SessionInfo session, int port)
    {
        _relay = relay;
        _session = session;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Completes when the page has asked for its session, i.e. someone opened it.</summary>
    public Task PageOpened => _pageOpened.Task;

    public bool Start()
    {
        try
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cancellation.Token); }
            catch (Exception) { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    {
                        await HandleAsync(client.GetStream());
                    }
                }
                catch (Exception)
                {
                    // A phone that closes the tab mid-request is not an error worth reporting.
                }
            });
        }
    }

    private async Task HandleAsync(Stream tls)
    {
        var request = await ReadRequestAsync(tls);
        if (request is null) return;

        switch (request.Value.Path)
        {
            case "/":
                await RespondAsync(tls, 200, "text/html; charset=utf-8", WebPage.Html);
                return;

            case "/simple-crypto.js":
                await RespondAsync(tls, 200, "text/javascript; charset=utf-8", SimpleCryptoScript());
                return;

            case "/api/session":
                _pageOpened.TrySetResult(true);
                await RespondAsync(tls, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new
                {
                    groupId = _session.GroupId,
                    groupKey = Convert.ToBase64String(_session.GroupKey),
                    hostDeviceId = _session.HostDeviceId,
                    hostName = _session.HostName,
                    deviceId = _session.DeviceId,
                    deviceName = _session.DeviceName
                }, CamelOptions));
                return;

            case "/api/signal":
            {
                var result = _relay.HandleMemberSignal(request.Value.Body);
                await RespondAsync(tls, result.Status, "application/json; charset=utf-8", result.Body);
                return;
            }

            case "/api/poll":
            {
                var result = _relay.HandleMemberPoll(request.Value.Body);
                await RespondAsync(tls, result.Status, "application/json; charset=utf-8", result.Body);
                return;
            }

            case "/api/log":
            {
                // The page reports what it did, so the operator can read one console instead of two.
                Console.WriteLine("[phone] " + request.Value.Body);
                await RespondAsync(tls, 200, "application/json; charset=utf-8", "{\"ok\":true}");
                return;
            }

            default:
                await RespondAsync(tls, 404, "text/plain; charset=utf-8", "not found");
                return;
        }
    }

    private static readonly JsonSerializerOptions CamelOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The page's own AES-GCM and SHA-256, served from an embedded copy of the source file.</summary>
    private static string SimpleCryptoScript()
    {
        using var stream = typeof(WebHost).Assembly.GetManifestResourceStream("TransferSelfTest.simple-crypto.js");
        if (stream is null) return "throw new Error('simple-crypto.js missing');";

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<Request?> ReadRequestAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        var headerEnd = -1;

        while (total < buffer.Length && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) return null;
            total += read;
            headerEnd = FindHeaderEnd(buffer, total);
        }

        if (headerEnd < 0) return null;

        var head = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
        length = Math.Clamp(length, 0, 64 * 1024);

        var body = new List<byte>(length);
        var already = total - (headerEnd + 4);
        for (var index = 0; index < Math.Min(already, length); index++) body.Add(buffer[headerEnd + 4 + index]);

        while (body.Count < length)
        {
            var chunk = new byte[Math.Min(8192, length - body.Count)];
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            body.AddRange(chunk.Take(read));
        }

        var path = parts[1];
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        return new Request(path, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index + 3 < length; index++)
            if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                return index;

        return -1;
    }

    private static async Task RespondAsync(Stream stream, int status, string contentType, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _listener.Stop(); } catch (Exception) { /* already closed */ }
        _cancellation.Dispose();
    }

    private readonly record struct Request(string Path, string Body);
}

/// <summary>What the page needs to know about the two ends of this test.</summary>
internal sealed record SessionInfo(
    string GroupId,
    byte[] GroupKey,
    string HostDeviceId,
    string HostName,
    string DeviceId,
    string DeviceName);
