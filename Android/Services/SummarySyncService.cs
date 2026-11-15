using System.Net;
using System.Text.Json;

namespace FairyAI_Android.Services;

/// <summary>
/// Polls a paired PC for task summaries and turns state changes into notifications.
///
/// Two rules shape this class. First, it is opt-in on both ends: the phone has to enable it here and
/// the PC has to enable it there, and without the token issued at pairing nothing is returned.
/// Second, it only ever reads four fields — id, title, state, time. A payload that grows extra
/// fields is ignored, so a future PC cannot start pushing task output into phone notifications by
/// accident.
/// </summary>
public sealed class SummarySyncService : IDisposable
{
    private const int MaxNotificationsPerPoll = 5;

    private readonly INotificationSink _sink;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<string, string> _knownStates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;

    public SummarySyncService(INotificationSink sink, HttpClient? http = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttpClient = http is null;
    }

    /// <summary>How often the loop polls while running.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    public bool IsRunning => _cts is not null;

    /// <summary>Notifications actually posted.</summary>
    public int Delivered { get; private set; }

    /// <summary>Polls attempted, including the ones that failed.</summary>
    public int Polls { get; private set; }

    public string? LastError { get; private set; }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        // Poll once immediately: waiting a full interval before the first result looks broken.
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not an error.
        }
    }

    public async Task<(bool Ok, string Message)> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var sync = ConfigManager.Load().Sync;

        if (!sync.SummarySyncEnabled)
            return Fail("摘要同步未开启");

        if (string.IsNullOrWhiteSpace(sync.SummarySyncEndpoint) || string.IsNullOrWhiteSpace(sync.SummarySyncToken))
            return Fail("还没有和电脑配对，或电脑没有下发令牌");

        if (!_sink.IsSupported)
            return Fail(_sink.LastError ?? "本机无法显示通知");

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = sync.SummarySyncEndpoint.TrimEnd('/') + "/summary";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-From-Device", sync.DeviceName);
            request.Headers.TryAddWithoutValidation("X-Device-Token", sync.SummarySyncToken);

            Polls++;
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var code = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var reason = code.Contains("summary_sync_disabled", StringComparison.Ordinal)
                    ? "电脑上没有开启摘要同步"
                    : "令牌已失效，需要重新配对";
                return Fail(reason);
            }

            if (!response.IsSuccessStatusCode)
                return Fail($"电脑返回 HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var delivered = Apply(body);
            LastError = null;
            return (true, delivered == 0 ? "已同步，没有状态变化" : $"已推送 {delivered} 条状态变化");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail($"连不上电脑：{ex.Message}");
        }
        catch (JsonException)
        {
            return Fail("电脑返回的内容无法解析");
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>Applies one payload. Returns how many notifications were posted.</summary>
    internal int Apply(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return 0;

        var delivered = 0;

        foreach (var task in tasks.EnumerateArray())
        {
            // Only these four fields are read; anything else in the object is ignored on purpose.
            var taskId = task.TryGetProperty("taskId", out var idElement) ? idElement.GetString() : null;
            var title = task.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var state = task.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(state)) continue;

            var changed = !_knownStates.TryGetValue(taskId, out var previous) || previous != state;
            var firstSight = previous is null;

            _knownStates[taskId] = state!;
            if (!changed) continue;

            // On first sight only report finished work: otherwise opening the app would replay a
            // notification for every task that was already running on the PC.
            if (firstSight && !IsTerminal(state!)) continue;
            if (delivered >= MaxNotificationsPerPoll) continue;

            var notification = new AppNotification(
                Title: "仙灵 · " + Describe(state!),
                Body: string.IsNullOrWhiteSpace(title) ? taskId : title!,
                Tag: taskId,
                Important: state is "Failed");

            if (_sink.Show(notification)) delivered++;
            else LastError = _sink.LastError;
        }

        Delivered += delivered;
        return delivered;
    }

    private static bool IsTerminal(string state) =>
        state is "Completed" or "Failed" or "Cancelled";

    private static string Describe(string state) => state switch
    {
        "Running" => "任务进行中",
        "Paused" => "任务已暂停",
        "Completed" => "任务完成",
        "Failed" => "任务失败",
        "Cancelled" => "任务已取消",
        _ => state
    };

    private (bool Ok, string Message) Fail(string reason)
    {
        LastError = reason;
        return (false, reason);
    }

    public void Dispose()
    {
        Stop();
        _pollGate.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }
}
