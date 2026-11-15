using System.Net;
using System.Text;
using System.Text.Json;

namespace FairyAI_Android.Services;

public sealed record PairedDeskTask(string TaskId, string Title, string State, bool CanPause, bool CanStop);

public sealed record PairedDeskPending(string InteractionId, string TaskId, string Kind, string Prompt, string? Risk);

public sealed record PairedDeskSnapshot(IReadOnlyList<PairedDeskTask> Tasks, IReadOnlyList<PairedDeskPending> Pending);

/// <summary>
/// The phone half of task control: it reads the paired PC's task list and answers its pending
/// approvals. The PC decides whether this is allowed at all (its own switch plus the token issued at
/// pairing), and every answer goes through the same pending item the desktop is showing.
///
/// Nothing long-lived travels here: no API keys, no tokens beyond the pairing token, no task output.
/// </summary>
public sealed class PairedDeskService
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PairedDeskService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsClient = http is null;
    }

    public string? LastError { get; private set; }

    public bool IsConfigured
    {
        get
        {
            var sync = ConfigManager.Load().Sync;
            return !string.IsNullOrWhiteSpace(sync.SummarySyncEndpoint) &&
                   !string.IsNullOrWhiteSpace(sync.SummarySyncToken);
        }
    }

    public async Task<(bool Ok, string Message, PairedDeskSnapshot? Desk)> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare("/desk");
        if (prepared.Error is not null) return (false, prepared.Error, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, prepared.Url);
            AddHeaders(request, prepared.Device, prepared.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var reason = DescribeRefusal(body);
                LastError = reason;
                return (false, reason, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = $"电脑返回 HTTP {(int)response.StatusCode}";
                LastError = message;
                return (false, message, null);
            }

            var desk = Parse(body);
            LastError = null;
            return (true, $"读到 {desk.Tasks.Count} 个任务、{desk.Pending.Count} 个待处理项", desk);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = $"连不上电脑：{ex.Message}";
            LastError = message;
            return (false, message, null);
        }
        catch (JsonException)
        {
            const string message = "电脑返回的内容无法解析";
            LastError = message;
            return (false, message, null);
        }
    }

    public Task<(bool Ok, string Message)> AnswerAsync(string interactionId, bool approved,
        CancellationToken cancellationToken = default) =>
        PostAnswerAsync(interactionId, approved, null, cancellationToken);

    public Task<(bool Ok, string Message)> AnswerTextAsync(string interactionId, string text,
        CancellationToken cancellationToken = default) =>
        PostAnswerAsync(interactionId, null, text, cancellationToken);

    private async Task<(bool Ok, string Message)> PostAnswerAsync(string interactionId, bool? approved,
        string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interactionId)) return (false, "缺少待决项 id");

        var prepared = Prepare("/desk/answer");
        if (prepared.Error is not null) return (false, prepared.Error);

        try
        {
            var payload = approved is not null
                ? JsonSerializer.Serialize(new { interactionId, approved = approved.Value })
                : JsonSerializer.Serialize(new { interactionId, text = text ?? "" });

            using var request = new HttpRequestMessage(HttpMethod.Post, prepared.Url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            AddHeaders(request, prepared.Device, prepared.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var reason = DescribeRefusal(body);
                LastError = reason;
                return (false, reason);
            }

            var message = ReadMessage(body) ?? (response.IsSuccessStatusCode ? "已提交" : $"电脑返回 HTTP {(int)response.StatusCode}");
            return (response.IsSuccessStatusCode, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = $"连不上电脑：{ex.Message}";
            LastError = message;
            return (false, message);
        }
    }

    private (string? Error, string Url, string Device, string Token) Prepare(string path)
    {
        var sync = ConfigManager.Load().Sync;

        if (string.IsNullOrWhiteSpace(sync.SummarySyncEndpoint) || string.IsNullOrWhiteSpace(sync.SummarySyncToken))
            return ("还没有和电脑配对：先在设置里配对一次，电脑才会下发令牌", "", "", "");

        return (null, sync.SummarySyncEndpoint.TrimEnd('/') + path, sync.DeviceName, sync.SummarySyncToken);
    }

    private static void AddHeaders(HttpRequestMessage request, string device, string token)
    {
        // Same credential the PC's paired endpoints require; a device name alone is not accepted.
        request.Headers.TryAddWithoutValidation("X-From-Device", device);
        request.Headers.TryAddWithoutValidation("X-Device-Token", token);
    }

    private static string DescribeRefusal(string body)
    {
        if (body.Contains("pair_control_unavailable", StringComparison.Ordinal))
            return "电脑上的这个功能没启用";
        if (body.Contains("summary_sync_disabled", StringComparison.Ordinal) ||
            body.Contains("pair_control_disabled", StringComparison.Ordinal))
            return "电脑上没有开启「手机查看任务与待审批」";
        if (body.Contains("unpaired", StringComparison.Ordinal))
            return "令牌已失效，需要重新配对";

        return "电脑拒绝了这次请求";
    }

    private static string? ReadMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var element) ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PairedDeskSnapshot Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var tasks = new List<PairedDeskTask>();
        if (root.TryGetProperty("tasks", out var tasksElement) && tasksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tasksElement.EnumerateArray())
            {
                var id = item.TryGetProperty("taskId", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                tasks.Add(new PairedDeskTask(
                    id!,
                    ReadString(item, "title") ?? id!,
                    ReadString(item, "state") ?? "",
                    item.TryGetProperty("canPause", out var canPause) && canPause.ValueKind == JsonValueKind.True,
                    item.TryGetProperty("canStop", out var canStop) && canStop.ValueKind == JsonValueKind.True));
            }
        }

        var pending = new List<PairedDeskPending>();
        if (root.TryGetProperty("pending", out var pendingElement) && pendingElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pendingElement.EnumerateArray())
            {
                var id = item.TryGetProperty("interactionId", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                pending.Add(new PairedDeskPending(
                    id!,
                    ReadString(item, "taskId") ?? "",
                    ReadString(item, "kind") ?? "",
                    ReadString(item, "prompt") ?? "",
                    ReadString(item, "risk")));
            }
        }

        return new PairedDeskSnapshot(tasks, pending);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
