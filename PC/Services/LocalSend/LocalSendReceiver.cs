using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.Relay;

namespace MyAiAssistant.Services.LocalSend;

public sealed record LocalSendReceiveOptions(
    string Alias,
    string DeviceModel,
    string DeviceType,
    string Fingerprint,
    int Port,
    string SaveDirectory)
{
    public int MaxFilesPerSession { get; init; } = 100;
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxSessionBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Asks the user whether to accept an incoming transfer. Returning false declines it.</summary>
public interface IReceiveConsent
{
    Task<bool> ConfirmAsync(LocalSendIncoming incoming, CancellationToken cancellationToken);
}

/// <summary>
/// Receives files over the LocalSend v2 protocol.
///
/// Nothing is written before the user agrees, and the sender's choices are treated as hostile
/// input: file names are sanitised (no traversal, no reserved names), declared sizes are checked
/// against limits before the first byte is accepted, and the stream is cut off if it exceeds what
/// was declared. A declined transfer answers 403, which is what the official client expects.
/// </summary>
public sealed class LocalSendReceiver : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxJsonBodyBytes = 256 * 1024;

    private readonly LocalSendReceiveOptions _options;
    private readonly IReceiveConsent _consent;
    private readonly object _sync = new();
    private readonly Dictionary<string, LocalSendReceiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _received = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _savedPaths = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private string? _activeSessionId;

    public LocalSendReceiver(LocalSendReceiveOptions options, IReceiveConsent consent)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
    }

    /// <summary>Raised after a transfer's files are all saved. Carries full paths on this machine.</summary>
    public event Action<IReadOnlyList<string>>? FilesReceived;

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public string DeviceInfoJson()
    {
        var payload = new
        {
            alias = _options.Alias,
            version = LocalSendProtocol.Version,
            deviceModel = _options.DeviceModel,
            deviceType = LocalSendProtocol.NormalizeDeviceType(_options.DeviceType),
            fingerprint = _options.Fingerprint,
            port = Port == 0 ? _options.Port : Port,
            protocol = "http",
            download = false
        };
        return JsonSerializer.Serialize(payload);
    }

    public bool Start(IPAddress? address = null, int? port = null)
    {
        if (_listener is not null) return true;
        try
        {
            var listener = new TcpListener(address ?? IPAddress.Any, port ?? _options.Port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptAsync(listener));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _listener = null;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public string? LastError { get; private set; }

    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        try { listener?.Stop(); } catch (Exception) { }
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
                        client.ReceiveTimeout = 60_000;
                        using var stream = client.GetStream();
                        var response = await HandleAsync(stream).ConfigureAwait(false);
                        await WriteResponseAsync(stream, response).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        await WriteResponseAsync(stream, new ReceivedResponse(400, $"{{\"message\":\"{ex.GetType().Name}\"}}"))
                            .ConfigureAwait(false);
                    }
                    catch (Exception) { }
                }
            });
        }
    }

    private sealed record ReceivedResponse(int Status, string Body, string ContentType = "application/json; charset=utf-8");

    private async Task<ReceivedResponse> HandleAsync(NetworkStream stream)
    {
        var head = await ReadHeadAsync(stream).ConfigureAwait(false);
        if (head is null) return new ReceivedResponse(400, "{}");

        var (rawHead, buffered) = head.Value;
        var lines = rawHead.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new ReceivedResponse(400, "{}");

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return new ReceivedResponse(400, "{}");
        var method = requestLine[0].ToUpperInvariant();
        var path = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        var query = ParseQuery(path);
        var route = path.Split('?')[0].TrimEnd('/');

        return (method, route) switch
        {
            ("GET", "/api/localsend/v2/info") => new ReceivedResponse(200, DeviceInfoJson()),
            ("POST", "/api/localsend/v2/register") => new ReceivedResponse(200, DeviceInfoJson()),
            ("POST", "/api/localsend/v2/prepare-upload") => await PrepareAsync(stream, headers, buffered).ConfigureAwait(false),
            ("POST", "/api/localsend/v2/upload") => await UploadAsync(stream, headers, buffered, query).ConfigureAwait(false),
            ("POST", "/api/localsend/v2/cancel") => Cancel(query),
            _ => new ReceivedResponse(404, "{}")
        };
    }

    private async Task<ReceivedResponse> PrepareAsync(
        NetworkStream stream, IReadOnlyDictionary<string, string> headers, byte[] buffered)
    {
        var length = ReadContentLength(headers);
        if (length is null or < 0 or > MaxJsonBodyBytes)
            return new ReceivedResponse(411, "{\"message\":\"invalid content length\"}");

        var body = await ReadBodyAsync(stream, buffered, length.Value, MaxJsonBodyBytes).ConfigureAwait(false);
        LocalSendPrepareRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LocalSendPrepareRequest>(Encoding.UTF8.GetString(body),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return new ReceivedResponse(400, "{\"message\":\"invalid json\"}"); }

        if (request is null || request.Files.Count == 0)
            return new ReceivedResponse(400, "{\"message\":\"no files\"}");

        lock (_sync)
        {
            ExpireSessions();
            if (_activeSessionId is not null)
                return new ReceivedResponse(409, "{\"message\":\"busy\"}");
        }

        if (request.Files.Count > _options.MaxFilesPerSession)
            return Decline($"文件数量超过上限（{request.Files.Count} > {_options.MaxFilesPerSession}）");

        var files = new Dictionary<string, LocalSendFile>(StringComparer.Ordinal);
        foreach (var (id, file) in request.Files)
        {
            if (string.IsNullOrWhiteSpace(id) || file is null) return Decline("文件条目无效");
            if (file.Size < 0) return Decline("文件大小无效");
            if (file.Size > _options.MaxFileBytes)
                return Decline($"单个文件超过上限（{Relay.RelayQuotaPolicy.Human(file.Size)}）");

            var safeName = LocalSendFileNaming.Sanitize(file.FileName);
            if (safeName is null) return Decline("文件名无效");

            files[id] = file with { FileName = safeName };
        }

        if (files.Values.Sum(file => file.Size) > _options.MaxSessionBytes)
            return Decline("本次传输合计超过上限");

        var alias = request.Info?.Alias ?? "未知设备";
        var fingerprint = request.Info?.Fingerprint ?? string.Empty;
        var incoming = new LocalSendIncoming(alias, fingerprint, files.Values.ToArray());

        bool accepted;
        try { accepted = await _consent.ConfirmAsync(incoming, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { accepted = false; } // fail closed: no answer is not consent

        if (!accepted) return Decline("用户拒绝接收");

        var tokens = files.Keys.ToDictionary(id => id, _ => LocalSendFileNaming.NewToken(), StringComparer.Ordinal);
        var session = new LocalSendReceiveSession(
            Guid.NewGuid().ToString("N")[..12], alias, fingerprint, files, tokens, DateTimeOffset.UtcNow);

        lock (_sync)
        {
            _sessions[session.SessionId] = session;
            _activeSessionId = session.SessionId;
        }

        var payload = JsonSerializer.Serialize(new
        {
            sessionId = session.SessionId,
            files = tokens
        });
        return new ReceivedResponse(200, payload);
    }

    private async Task<ReceivedResponse> UploadAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        byte[] buffered,
        IReadOnlyDictionary<string, string> query)
    {
        var sessionId = query.GetValueOrDefault("sessionId");
        var fileId = query.GetValueOrDefault("fileId");
        var token = query.GetValueOrDefault("token");
        if (sessionId is null || fileId is null || token is null)
            return new ReceivedResponse(403, "{\"message\":\"missing parameters\"}");

        LocalSendReceiveSession? session;
        LocalSendFile? file;
        lock (_sync)
        {
            ExpireSessions();
            if (!_sessions.TryGetValue(sessionId, out session)) return new ReceivedResponse(403, "{}");
            if (!session.Tokens.TryGetValue(fileId, out var expected) ||
                !CryptographicEquals(expected, token))
                return new ReceivedResponse(403, "{}");
            if (!session.Files.TryGetValue(fileId, out file)) return new ReceivedResponse(403, "{}");
            // A replay of an already-accepted file is refused; accepting it twice would let a
            // sender overwrite a file the user already agreed to receive once.
            if (_received.TryGetValue(sessionId, out var done) && done.Contains(fileId))
                return new ReceivedResponse(403, "{}");
        }

        var declared = file.Size;
        var length = ReadContentLength(headers);
        if (length is null) return new ReceivedResponse(411, "{}");
        if (length.Value > declared || length.Value > _options.MaxFileBytes)
            return new ReceivedResponse(400, "{\"message\":\"size exceeds the declared size\"}");

        Directory.CreateDirectory(_options.SaveDirectory);
        var unique = LocalSendFileNaming.MakeUnique(_options.SaveDirectory, file.FileName);
        var destination = Path.Combine(_options.SaveDirectory, unique);
        if (!IsInsideSaveDirectory(destination)) return new ReceivedResponse(403, "{}");

        var temporary = destination + ".part";
        long written = 0;
        try
        {
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var initial = (int)Math.Min(buffered.Length, length.Value);
                if (initial > 0)
                {
                    await target.WriteAsync(buffered.AsMemory(0, initial)).ConfigureAwait(false);
                    written = initial;
                }
                written = await CopyAsync(stream, target, length.Value, written, _options.MaxFileBytes)
                    .ConfigureAwait(false);
            }

            if (written != length.Value)
            {
                TryDelete(temporary);
                return new ReceivedResponse(400, "{\"message\":\"truncated upload\"}");
            }

            File.Move(temporary, destination, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return new ReceivedResponse(500, "{\"message\":\"write failed\"}");
        }

        string[]? completed = null;
        lock (_sync)
        {
            session = _sessions[sessionId];
            if (!_received.TryGetValue(sessionId, out var received))
            {
                received = new HashSet<string>(StringComparer.Ordinal);
                _received[sessionId] = received;
            }
            received.Add(fileId);

            if (!_savedPaths.TryGetValue(sessionId, out var saved))
            {
                saved = new List<string>();
                _savedPaths[sessionId] = saved;
            }
            saved.Add(destination);

            if (received.Count == session.Files.Count)
            {
                completed = saved.ToArray();
                _sessions.Remove(sessionId);
                _received.Remove(sessionId);
                _savedPaths.Remove(sessionId);
                if (_activeSessionId == sessionId) _activeSessionId = null;
            }
        }

        if (completed is not null) FilesReceived?.Invoke(completed);

        return new ReceivedResponse(200, "{}");
    }

    private ReceivedResponse Cancel(IReadOnlyDictionary<string, string> query)
    {
        var sessionId = query.GetValueOrDefault("sessionId");
        if (sessionId is null) return new ReceivedResponse(400, "{}");
        lock (_sync)
        {
            if (_sessions.Remove(sessionId) && _activeSessionId == sessionId) _activeSessionId = null;
            _received.Remove(sessionId);
            _savedPaths.Remove(sessionId);
        }
        return new ReceivedResponse(200, "{}");
    }

    private static ReceivedResponse Decline(string reason)
    {
        var payload = JsonSerializer.Serialize(new { message = reason });
        return new ReceivedResponse(403, payload);
    }

    private void ExpireSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.SessionLifetime;
        foreach (var expired in _sessions.Where(pair => pair.Value.CreatedAt < cutoff).Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(expired);
            _received.Remove(expired);
            _savedPaths.Remove(expired);
            if (_activeSessionId == expired) _activeSessionId = null;
        }
    }

    private bool IsInsideSaveDirectory(string path)
    {
        try
        {
            var root = Path.GetFullPath(_options.SaveDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static bool CryptographicEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static int? ReadContentLength(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var value) ? value : null;

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream, byte[] buffered, int length, int maxBytes)
    {
        if (length > maxBytes) length = maxBytes;
        var body = new byte[length];
        var copied = Math.Min(buffered.Length, length);
        Array.Copy(buffered, body, copied);
        var offset = copied;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset)).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return offset == length ? body : body[..offset];
    }

    private static async Task<long> CopyAsync(
        NetworkStream source, Stream target, long declaredLength, long alreadyWritten, long maxBytes)
    {
        var buffer = new byte[64 * 1024];
        var written = alreadyWritten;
        while (written < declaredLength && written <= maxBytes)
        {
            var want = (int)Math.Min(buffer.Length, declaredLength - written);
            var read = await source.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            written += read;
        }
        return written;
    }

    private static async Task<(string Head, byte[] Buffered)?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            for (var i = 0; i + 3 < total; i++)
            {
                if (buffer[i] != '\r' || buffer[i + 1] != '\n' || buffer[i + 2] != '\r' || buffer[i + 3] != '\n')
                    continue;
                var head = Encoding.UTF8.GetString(buffer, 0, i);
                var rest = new byte[total - (i + 4)];
                Array.Copy(buffer, i + 4, rest, 0, rest.Length);
                return (head, rest);
            }
        }
        return null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, ReceivedResponse response)
    {
        var payload = Encoding.UTF8.GetBytes(response.Body);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n")
            .Append($"Content-Type: {response.ContentType}\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        411 => "Length Required",
        500 => "Internal Server Error",
        _ => "Error"
    };

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = path.IndexOf('?');
        if (index < 0) return result;
        foreach (var pair in path[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync) _sessions.Clear();
    }
}
