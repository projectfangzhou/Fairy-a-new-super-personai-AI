using System.Net.Http.Headers;
using System.Text;

namespace FairyAI_Android.Services.Notes;

/// <summary>
/// One sync round against the paired computer: POST the watermark and everything changed since, get
/// back what the computer changed after the same watermark.
///
/// The same per-device token the pairing handed out is used — notes are as private as the task
/// summaries, so they ride the same authenticated channel rather than inventing a second one.
/// </summary>
public sealed class NoteSyncClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public NoteSyncClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = http is null;
    }

    public async Task<(bool Ok, string Message, NoteSyncResponse? Response)> SyncAsync(
        string endpoint,
        string deviceName,
        string token,
        long since,
        IEnumerable<Note> changed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "还没有配对电脑的地址", null);

        try
        {
            var url = endpoint.TrimEnd('/') + "/notes/sync";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    NoteSyncWire.BuildRequestBody(since, changed, deviceName),
                    Encoding.UTF8,
                    "application/json"),
            };

            request.Headers.TryAddWithoutValidation("X-From-Device", deviceName ?? "");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("X-Device-Token", token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var (ok, error, parsed) = NoteSyncWire.ParseResponse((int)response.StatusCode, body);
            if (!ok || parsed is null) return (false, error ?? "电脑返回了无法解析的内容", null);

            return (true, "", parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "这一轮同步被取消了", null);
        }
        catch (TaskCanceledException)
        {
            return (false, "连电脑超时（20 秒）", null);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"连不上电脑：{ex.Message}", null);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
