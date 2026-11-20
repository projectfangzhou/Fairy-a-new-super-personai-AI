using System.Net.Http.Headers;
using System.Text;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Calls an OpenAI-compatible <c>/embeddings</c> endpoint.
///
/// Nothing else here: the request shape and the reply parsing live in <see cref="EmbeddingsWire"/> so
/// they can be asserted without a network, and this class only adds the HTTP parts.
/// </summary>
public sealed class EmbeddingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public EmbeddingClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
    }

    /// <summary>Embeds every input, calling the endpoint in batches. One failed batch fails the call.</summary>
    public async Task<EmbeddingBatch> EmbedAsync(
        EmbeddingSettings settings,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsUsable) return EmbeddingBatch.Fail("没有配置向量模型");

        var texts = (inputs ?? Array.Empty<string>()).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        if (texts.Count == 0) return new EmbeddingBatch(true, Array.Empty<float[]>(), null);

        var all = new List<float[]>(texts.Count);

        for (var start = 0; start < texts.Count; start += EmbeddingsWire.MaxInputsPerCall)
        {
            var batch = texts.Skip(start).Take(EmbeddingsWire.MaxInputsPerCall).ToList();
            var result = await EmbedBatchAsync(settings, batch, cancellationToken).ConfigureAwait(false);

            if (!result.Ok) return result;
            all.AddRange(result.Vectors);
        }

        return new EmbeddingBatch(true, all, null);
    }

    private async Task<EmbeddingBatch> EmbedBatchAsync(
        EmbeddingSettings settings,
        List<string> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl)
            {
                Content = new StringContent(EmbeddingsWire.BuildBody(settings, batch), Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var (ok, vectors, error) = EmbeddingsWire.ParseResponse((int)response.StatusCode, body, batch.Count);
            return ok ? new EmbeddingBatch(true, vectors, null) : EmbeddingBatch.Fail(error ?? "向量接口没有回应");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EmbeddingBatch.Fail("向量计算被取消了");
        }
        catch (TaskCanceledException)
        {
            return EmbeddingBatch.Fail("向量接口超时了（30 秒没有回应）");
        }
        catch (HttpRequestException ex)
        {
            return EmbeddingBatch.Fail($"连不上向量接口：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
