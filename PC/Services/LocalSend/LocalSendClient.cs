using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>
/// Sends files to another LocalSend device using the v2 flow: prepare-upload to get a session
/// and one token per file, then one upload per file.
///
/// It talks only to the endpoint the peer announced, it never invents a session, and it reports
/// which file failed instead of pretending the batch worked.
/// </summary>
public sealed class LocalSendClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LocalSendClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsClient = http is null;
    }

    public async Task<(bool Ok, string Message, LocalSendPrepareResponse? Response)> PrepareAsync(
        LocalSendDevice peer,
        LocalSendDevice self,
        IReadOnlyDictionary<string, LocalSendFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(files);
        if (peer.Endpoint is null) return (false, "对端没有可用地址", null);
        if (files.Count == 0) return (false, "没有要发送的文件", null);

        var request = new LocalSendPrepareRequest
        {
            Info = new LocalSendDeviceInfo(
                self.Alias, LocalSendProtocol.Version, self.DeviceModel,
                self.DeviceType, self.Fingerprint, self.Port, self.Protocol, self.Download),
            Files = new Dictionary<string, LocalSendFile>(files, StringComparer.Ordinal)
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http
                .PostAsync($"{peer.Endpoint}/api/localsend/v2/prepare-upload", content, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return (false, "对方已拒绝这次传输", null);
            if (!response.IsSuccessStatusCode)
                return (false, $"对端返回 HTTP {(int)response.StatusCode}", null);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<LocalSendPrepareResponse>(body, JsonOptions);
            if (parsed?.SessionId is null || parsed.Files.Count == 0)
                return (false, "对端没有返回会话或文件令牌", null);
            if (parsed.Files.Values.Any(string.IsNullOrWhiteSpace))
                return (false, "对端返回了空令牌", null);

            return (true, "已建立传输会话", parsed);
        }
        catch (OperationCanceledException) { return (false, "传输已取消", null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"无法连接对端：{ex.GetType().Name}", null);
        }
    }

    /// <summary>
    /// Full transfer of the given paths. Returns how many files actually arrived; a partial
    /// batch is reported as a failure with the count, never as success.
    /// </summary>
    public async Task<LocalSendResult> SendFilesAsync(
        LocalSendDevice peer,
        LocalSendDevice self,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var described = LocalSendProtocol.DescribeFiles(paths);
        if (described.Count == 0) return new LocalSendResult(false, "没有可发送的文件");

        var (prepared, message, response) = await PrepareAsync(peer, self, described, cancellationToken)
            .ConfigureAwait(false);
        if (!prepared || response is null) return new LocalSendResult(false, message, null, 0);

        var uploaded = 0;
        foreach (var (fileId, token) in response.Files)
        {
            if (!described.TryGetValue(fileId, out var file))
                return new LocalSendResult(false, $"对端返回了未知文件令牌：{fileId}", response.SessionId, uploaded);

            var path = paths.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), file.FileName, StringComparison.Ordinal));
            if (path is null || !File.Exists(path))
                return new LocalSendResult(false, $"找不到源文件：{file.FileName}", response.SessionId, uploaded);

            try
            {
                await using var stream = File.OpenRead(path);
                using var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var url = $"{peer.Endpoint}/api/localsend/v2/upload" +
                          $"?sessionId={Uri.EscapeDataString(response.SessionId!)}" +
                          $"&fileId={Uri.EscapeDataString(fileId)}" +
                          $"&token={Uri.EscapeDataString(token)}";

                using var upload = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
                if (!upload.IsSuccessStatusCode)
                    return new LocalSendResult(false,
                        $"发送 {file.FileName} 失败：HTTP {(int)upload.StatusCode}",
                        response.SessionId, uploaded);

                uploaded++;
            }
            catch (OperationCanceledException)
            {
                return new LocalSendResult(false, "传输已取消", response.SessionId, uploaded);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                return new LocalSendResult(false,
                    $"发送 {file.FileName} 失败：{ex.GetType().Name}", response.SessionId, uploaded);
            }
        }

        return new LocalSendResult(true, $"已发送 {uploaded} 个文件", response.SessionId, uploaded);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
