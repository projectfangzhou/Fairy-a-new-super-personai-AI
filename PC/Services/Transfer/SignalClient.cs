using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.Transfer;

public sealed record ReceivedSignal(string From, TransferSignal Signal);

public sealed record SignalSendResult(bool Ok, long Id, string Error);

public sealed record SignalPollResult(bool Ok, IReadOnlyList<ReceivedSignal> Signals, string Error);

/// <summary>
/// The member side of signalling: it posts sealed messages to the host device it paired with, and
/// polls for the ones addressed to it.
///
/// Only the group key can produce a message the host will relay, and only the group key can open
/// the answer. The host itself cannot read the SDP it is relaying — it verifies membership and
/// freshness, then passes the sealed blob along.
/// </summary>
public sealed class SignalClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Uri _base;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly byte[] _groupKey;

    public SignalClient(string baseUrl, string groupId, string deviceId, string hostDeviceId, byte[] groupKey, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) && http is null)
            throw new ArgumentException("需要给出对端地址", nameof(baseUrl));
        ArgumentNullException.ThrowIfNull(groupKey);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("设备组不能为空", nameof(groupId));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("本机编号不能为空", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(hostDeviceId)) throw new ArgumentException("对端编号不能为空", nameof(hostDeviceId));

        _base = string.IsNullOrWhiteSpace(baseUrl) ? new Uri("http://127.0.0.1/") : new Uri(baseUrl);
        _groupKey = groupKey;
        GroupId = groupId;
        DeviceId = deviceId;
        HostDeviceId = hostDeviceId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
    }

    public string GroupId { get; }

    public string DeviceId { get; }

    /// <summary>The device running the pairing endpoint — the relay for this group.</summary>
    public string HostDeviceId { get; }

    /// <summary>The highest signal id already received, so a poll never fetches the same message twice.</summary>
    public long LastSeenId { get; set; }

    public async Task<SignalSendResult> SendAsync(string toDeviceId, TransferSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(toDeviceId)) return new SignalSendResult(false, 0, "缺少接收方");

        SignalEnvelope envelope;
        try
        {
            envelope = TransferSignals.Seal(_groupKey, GroupId, DeviceId, toDeviceId, signal, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException ex)
        {
            return new SignalSendResult(false, 0, ex.Message);
        }

        var content = new StringContent(envelope.ToJson(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.PostAsync(new Uri(_base, "/pairing/signal"), content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new SignalSendResult(false, 0, ReadMessage(body) ?? $"HTTP {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(body);
            var id = document.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
            return new SignalSendResult(true, id, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SignalSendResult(false, 0, $"发送失败：{ex.GetType().Name}");
        }
    }

    /// <summary>Collects whatever arrived since the last poll. Empty is a normal answer, not an error.</summary>
    public async Task<SignalPollResult> PollAsync(CancellationToken cancellationToken = default)
    {
        var request = TransferSignals.SealControl(_groupKey, GroupId, DeviceId, HostDeviceId, "poll",
            $"{{\"since\":{LastSeenId}}}", DateTimeOffset.UtcNow);

        var content = new StringContent(request.ToJson(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.PostAsync(new Uri(_base, "/pairing/signal/poll"), content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new SignalPollResult(false, Array.Empty<ReceivedSignal>(), ReadMessage(body) ?? $"HTTP {(int)response.StatusCode}");

            var signals = new List<ReceivedSignal>();
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return new SignalPollResult(true, signals, string.Empty);

            var now = DateTimeOffset.UtcNow;
            foreach (var message in messages.EnumerateArray())
            {
                var id = message.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
                var raw = message.TryGetProperty("envelope", out var envelopeElement) ? envelopeElement.GetString() : null;
                if (id <= LastSeenId) continue;

                LastSeenId = id;

                var envelope = SignalEnvelope.Parse(raw);
                if (envelope is null) continue;

                if (TransferSignals.TryOpenSignal(_groupKey, envelope, now, DeviceId, out var signal, out _) && signal is not null)
                    signals.Add(new ReceivedSignal(envelope.From, signal));
            }

            return new SignalPollResult(true, signals, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SignalPollResult(false, Array.Empty<ReceivedSignal>(), $"轮询失败：{ex.GetType().Name}");
        }
    }

    private static string? ReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
