using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAiAssistant.Services.Http;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Transfer;

namespace MyAiAssistant.Services;

/// <summary>
/// The network door a device knocks on to join a group.
///
/// It is deliberately the whole surface: one path, POST only, a small body, and every request
/// authorised by the pairing code rather than by anything the caller can claim about itself.
/// A wrong guess costs an attempt, the invite caps the total, and the address caps the rate, so
/// the six digits are not the only thing standing between the LAN and a group key.
///
/// Nothing else is exposed here: no listing of groups, no device discovery, no file access. A
/// paired device talks over its own authenticated channel once it holds the group key.
/// </summary>
/// <summary>One member call handled without a socket: the status and body an HTTP route would send.</summary>
public sealed record MemberCallResult(int Status, string Body);

public sealed class PairingEndpointServer
{
    public const int DefaultPort = 9879;

    /// <summary>
    /// Per-address ceiling over ten minutes. The invite already caps guessing at five tries, so
    /// this is about how much work one machine may hand us, not about the odds of a lucky guess —
    /// which is why it is looser than the invite's own limit.
    /// </summary>
    private const int MaxAttemptsPerWindow = 20;
    private const int MaxBodyBytes = 16 * 1024;
    private const string JoinPath = "/pairing/join";
    private const string SignalPath = "/pairing/signal";
    private const string SignalPollPath = "/pairing/signal/poll";
    private const int MaxSignalRequestsPerWindow = 600;

    /// <summary>
    /// Polling is how a paired device waits for signalling, so it is expected to be frequent —
    /// several times a second while a connection is being set up. It gets its own, much looser
    /// budget than the messages themselves, or a normal client throttles itself within seconds.
    /// </summary>
    private const int MaxPollRequestsPerWindow = 12000;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PairingService _pairing;
    private readonly LocalHttpServer _server;
    private readonly Dictionary<string, Attempts> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Attempts> _signalAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Attempts> _pollAttempts = new(StringComparer.Ordinal);
    private readonly SignalMailbox _mailbox = new();
    private readonly SignalReplayGuard _replay = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _configuredPort;

    public PairingEndpointServer(PairingService pairing, int port = DefaultPort, Func<DateTimeOffset>? clock = null)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _configuredPort = port < 0 ? DefaultPort : port;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _server = new LocalHttpServer(HandleAsync, MaxBodyBytes);
    }

    public bool IsRunning => _server.IsRunning;

    public int Port => _server.IsRunning ? _server.Port : _configuredPort;

    public string? LastError => _server.LastError;

    /// <summary>True when this surface is reachable from another machine, which the UI should say.</summary>
    public bool IsReachableFromNetwork => _server.IsReachableFromNetwork;

    public bool Start(IPAddress? address = null) => _server.Start(_configuredPort, address ?? IPAddress.Any);

    public void Stop() => _server.Stop();

    /// <summary>
    /// Puts a sealed signal in a member's inbox without going through the network.
    ///
    /// The device that owns the relay is not a member of its own group, so it cannot authorise a
    /// request against itself. It does not need to: it already holds the group key and the
    /// mailbox, and this is the in-process equivalent of posting to itself.
    /// </summary>
    public long EnqueueSignal(string groupId, string to, string from, TransferSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(from))
            throw new ArgumentException("收发双方都不能为空");

        var key = _pairing.Store.TryGetGroupKey(groupId)
                  ?? throw new InvalidOperationException("设备组密钥不可用");

        try
        {
            var envelope = TransferSignals.Seal(key, groupId, from, to, signal, _clock());
            return _mailbox.Post(groupId, to, from, envelope.ToJson());
        }
        finally
        {
            Array.Clear(key);
        }
    }

    /// <summary>
    /// Reads what is waiting for one device, opened. Only the host calls this, for itself: a
    /// member's inbox is reachable over the authenticated poll route, not from here.
    /// </summary>
    public IReadOnlyList<ReceivedSignal> TakeSignals(string groupId, string deviceId, ref long since)
    {
        var key = _pairing.Store.TryGetGroupKey(groupId);
        if (key is null) return Array.Empty<ReceivedSignal>();

        var collected = new List<ReceivedSignal>();
        try
        {
            foreach (var delivery in _mailbox.Since(groupId, deviceId, since))
            {
                since = Math.Max(since, delivery.Id);

                var envelope = SignalEnvelope.Parse(delivery.Payload);
                if (envelope is null) continue;

                if (TransferSignals.TryOpenSignal(key, envelope, _clock(), deviceId, out var signal, out _) &&
                    signal is not null)
                {
                    collected.Add(new ReceivedSignal(envelope.From, signal));
                }
            }
        }
        finally
        {
            Array.Clear(key);
        }

        return collected;
    }

    /// <summary>Applies a new port. A listener that is already up is rebound so the change is not a lie.</summary>
    public bool Reconfigure(int port)
    {
        if (port < 0) return false;
        var wasRunning = _server.IsRunning;
        if (wasRunning) _server.Stop();

        _configuredPort = port;
        return !wasRunning || Start();
    }

    private Task<HttpResponseData> HandleAsync(HttpRequestData request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(NotFound());

        var path = Normalize(request.Path);

        if (string.Equals(path, JoinPath, StringComparison.OrdinalIgnoreCase)) return HandleJoinAsync(request);
        if (string.Equals(path, SignalPath, StringComparison.OrdinalIgnoreCase)) return HandleSignalAsync(request);
        if (string.Equals(path, SignalPollPath, StringComparison.OrdinalIgnoreCase)) return HandleSignalPollAsync(request);

        return Task.FromResult(NotFound());
    }

    private Task<HttpResponseData> HandleJoinAsync(HttpRequestData request)
    {
        if (OverRateLimit(request.RemoteAddress))
        {
            return Task.FromResult(new HttpResponseData(429,
                Serialize(new { ok = false, error = "rate-limited", message = "请求过于频繁，请稍后再试" })));
        }

        JoinEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<JoinEnvelope>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return Task.FromResult(BadRequest("配对请求格式不正确"));
        }

        if (envelope?.Request is null) return Task.FromResult(BadRequest("配对请求为空"));
        if (string.IsNullOrWhiteSpace(envelope.Code)) return Task.FromResult(BadRequest("缺少配对码"));

        var result = _pairing.HandleJoin(envelope.Code, envelope.Request);
        if (!result.Ok || result.Response is null)
        {
            // 403 for anything the code or the invite refused: the caller cannot tell from the
            // status whether the group exists, only from our wording.
            return Task.FromResult(new HttpResponseData(403,
                Serialize(new { ok = false, error = result.Status.Error, message = result.Status.Message })));
        }

        return Task.FromResult(new HttpResponseData(200, Serialize(new { ok = true, response = result.Response })));
    }

    /// <summary>
    /// Relays one sealed signal to the device it names. The host cannot read the SDP — it only
    /// checks that both ends are members of the group, that the message is fresh, and that it has
    /// not been seen before.
    /// </summary>
    private Task<HttpResponseData> HandleSignalAsync(HttpRequestData request)
    {
        if (OverSignalRateLimit(request.RemoteAddress))
            return Task.FromResult(new HttpResponseData(429,
                Serialize(new { ok = false, error = "rate-limited", message = "信令请求过于频繁" })));

        var result = HandleMemberSignal(request.Body);
        return Task.FromResult(new HttpResponseData(result.Status, result.Body));
    }

    /// <summary>
    /// Returns everything addressed to the polling member after the id it already has. Signalling
    /// that arrived while it was not looking is waiting here, sealed and briefly kept.
    /// </summary>
    private Task<HttpResponseData> HandleSignalPollAsync(HttpRequestData request)
    {
        if (OverPollRateLimit(request.RemoteAddress))
            return Task.FromResult(new HttpResponseData(429,
                Serialize(new { ok = false, error = "rate-limited", message = "轮询过于频繁" })));

        var result = HandleMemberPoll(request.Body);
        return Task.FromResult(new HttpResponseData(result.Status, result.Body));
    }

    /// <summary>
    /// One member's relayed signal, handled without a socket.
    ///
    /// This is the same authorisation as the HTTP route — the caller still has to be a member and
    /// still has to produce a sealed, fresh, unreplayed envelope. It exists so a client that the
    /// app itself serves (the phone page in the self-test) can use the relay without the app having
    /// to POST to itself.
    /// </summary>
    public MemberCallResult HandleMemberSignal(string bodyJson)
    {
        var envelope = SignalEnvelope.Parse(bodyJson);
        if (envelope is null) return new MemberCallResult(400,
            Serialize(new { ok = false, error = PairingErrors.MalformedRequest, message = "信令格式不正确" }));

        if (!TryAuthorize(envelope.GroupId, envelope.From, envelope.To, out var groupKey, out var authError))
            return Refuse(authError);

        try
        {
            if (!TransferSignals.TryOpen(groupKey, envelope, _clock(), envelope.To, out var kind, out _, out var openError))
                return Refuse(openError);

            if (!Enum.TryParse<TransferSignalKind>(kind, ignoreCase: true, out _))
                return Refuse($"这不是连接信令：{kind}");

            if (!_replay.Accept(envelope.GroupId, envelope.From, envelope.Nonce))
                return Refuse("重复的信令被拒绝");

            var id = _mailbox.Post(envelope.GroupId, envelope.To, envelope.From, envelope.ToJson());
            return new MemberCallResult(200, Serialize(new { ok = true, id }));
        }
        finally
        {
            Array.Clear(groupKey);
        }
    }

    /// <summary>One member's poll, handled without a socket. Same authorisation as the HTTP route.</summary>
    public MemberCallResult HandleMemberPoll(string bodyJson)
    {
        var envelope = SignalEnvelope.Parse(bodyJson);
        if (envelope is null) return new MemberCallResult(400,
            Serialize(new { ok = false, error = PairingErrors.MalformedRequest, message = "信令格式不正确" }));

        var hostDeviceId = _pairing.Store.Identity.DeviceId;
        if (!string.Equals(envelope.To, hostDeviceId, StringComparison.Ordinal))
            return Refuse("轮询请求没有发给本机");

        if (!TryAuthorize(envelope.GroupId, envelope.From, hostDeviceId, out var groupKey, out var authError))
            return Refuse(authError);

        try
        {
            if (!TransferSignals.TryOpen(groupKey, envelope, _clock(), hostDeviceId, out var kind, out var value, out var openError))
                return Refuse(openError);

            if (!string.Equals(kind, "poll", StringComparison.OrdinalIgnoreCase))
                return Refuse("这不是轮询请求");

            long since = 0;
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.TryGetProperty("since", out var sinceElement)) since = sinceElement.GetInt64();
            }
            catch (JsonException)
            {
                return new MemberCallResult(400, Serialize(new { ok = false, error = PairingErrors.MalformedRequest, message = "轮询请求内容无法解析" }));
            }

            var messages = _mailbox.Since(envelope.GroupId, envelope.From, since)
                .Select(delivery => new { id = delivery.Id, from = delivery.From, envelope = delivery.Payload })
                .ToArray();

            return new MemberCallResult(200, Serialize(new { ok = true, messages }));
        }
        finally
        {
            Array.Clear(groupKey);
        }
    }

    /// <summary>
    /// Both ends of a signal have to be in the group. The relay is not a public bulletin board:
    /// being able to reach the port buys nothing on its own.
    /// </summary>
    private bool TryAuthorize(string groupId, string from, string to, out byte[] groupKey, out string error)
    {
        groupKey = Array.Empty<byte>();

        var group = _pairing.Store.Find(groupId);
        if (group is null)
        {
            error = "设备组不存在";
            return false;
        }

        if (!group.Members.Any(member => string.Equals(member.DeviceId, from, StringComparison.Ordinal)))
        {
            error = "发送方不在这个设备组里";
            return false;
        }

        var toIsHost = string.Equals(to, _pairing.Store.Identity.DeviceId, StringComparison.Ordinal);
        if (!toIsHost && !group.Members.Any(member => string.Equals(member.DeviceId, to, StringComparison.Ordinal)))
        {
            error = "接收方不在这个设备组里";
            return false;
        }

        var key = _pairing.Store.TryGetGroupKey(groupId);
        if (key is null)
        {
            error = "设备组密钥不可用";
            return false;
        }

        groupKey = key;
        error = string.Empty;
        return true;
    }

    /// <summary>An in-process member call that was refused, with the reason to show.</summary>
    private static MemberCallResult Refuse(string message) =>
        new(403, Serialize(new { ok = false, error = "refused", message }));

    private bool OverRateLimit(string remoteAddress)
    {
        var key = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
        var now = _clock();

        lock (_gate)
        {
            foreach (var stale in _attempts.Where(pair => now >= pair.Value.Reset).Select(pair => pair.Key).ToArray())
                _attempts.Remove(stale);

            if (!_attempts.TryGetValue(key, out var entry))
            {
                _attempts[key] = new Attempts { Count = 1, Reset = now + AttemptWindow };
                return false;
            }

            entry.Count++;
            return entry.Count > MaxAttemptsPerWindow;
        }
    }

    /// <summary>
    /// Signalling is chatty — a connection attempt is many small messages — so it gets a different
    /// budget from joins, which are rare and worth throttling hard.
    /// </summary>
    private bool OverSignalRateLimit(string remoteAddress)
    {
        var key = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
        var now = _clock();

        lock (_gate)
        {
            foreach (var stale in _signalAttempts.Where(pair => now >= pair.Value.Reset).Select(pair => pair.Key).ToArray())
                _signalAttempts.Remove(stale);

            if (!_signalAttempts.TryGetValue(key, out var entry))
            {
                _signalAttempts[key] = new Attempts { Count = 1, Reset = now + AttemptWindow };
                return false;
            }

            entry.Count++;
            return entry.Count > MaxSignalRequestsPerWindow;
        }
    }

    /// <summary>
    /// Polling has its own budget: it is meant to be frequent, and the point of a limit here is only
    /// to stop a runaway client, not to shape normal traffic.
    /// </summary>
    private bool OverPollRateLimit(string remoteAddress)
    {
        var key = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
        var now = _clock();

        lock (_gate)
        {
            foreach (var stale in _pollAttempts.Where(pair => now >= pair.Value.Reset).Select(pair => pair.Key).ToArray())
                _pollAttempts.Remove(stale);

            if (!_pollAttempts.TryGetValue(key, out var entry))
            {
                _pollAttempts[key] = new Attempts { Count = 1, Reset = now + AttemptWindow };
                return false;
            }

            entry.Count++;
            return entry.Count > MaxPollRequestsPerWindow;
        }
    }

    private static string Normalize(string path)
    {
        var query = path.IndexOf('?');
        var clean = query >= 0 ? path[..query] : path;
        return clean.StartsWith('/') ? clean : "/" + clean;
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static HttpResponseData BadRequest(string message) =>
        new(400, Serialize(new { ok = false, error = PairingErrors.MalformedRequest, message }));

    private static HttpResponseData NotFound() =>
        new(404, Serialize(new { ok = false, error = "not-found", message = "没有这个接口" }));

    private sealed class Attempts
    {
        public int Count { get; set; }

        public DateTimeOffset Reset { get; set; }
    }

    internal sealed class JoinEnvelope
    {
        public string? Code { get; set; }

        public PairingRequest? Request { get; set; }
    }
}
