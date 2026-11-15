using System.Net.Http.Headers;
using System.Text;

namespace FairyAI_Android.Services.Vision;

/// <summary>
/// Calls the vision endpoint. The endpoint is read through a delegate rather than captured at
/// construction, because the user can change the model in settings without restarting the app.
/// </summary>
public sealed class VisionClient : IDisposable
{
    private readonly Func<VisionTarget?> _target;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public VisionClient(Func<VisionTarget?> target, HttpClient? http = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _ownsHttp = http is null;
    }

    /// <summary>Last endpoint name used, for status text that does not leak a key.</summary>
    public string? LastTargetName { get; private set; }

    /// <summary>
    /// The endpoint that would be used right now, or null when none is configured. Callers check this
    /// before capturing: asking the user to approve a screenshot the app cannot analyse is a waste of
    /// their attention.
    /// </summary>
    public VisionTarget? ResolveTarget() => _target();

    public async Task<VisionAnswer> DescribeAsync(
        byte[]? image,
        string? question,
        CancellationToken cancellationToken = default)
    {
        if (image is null || image.Length == 0)
            return VisionAnswer.Fail("没有画面可以分析：没有拿到截图。");

        var target = _target();
        if (target is null)
            return VisionAnswer.Fail(VisionTargetSelector.MissingReason, image.Length);

        LastTargetName = target.Name;

        var (status, body, transportError) =
            await SendAsync(target, image, VisionRequest.BuildPrompt(question), cancellationToken).ConfigureAwait(false);

        if (transportError is not null) return VisionAnswer.Fail(transportError, image.Length);

        var (ok, text, error) = VisionReply.Parse(status, body);
        return ok ? new VisionAnswer(true, text, null, image.Length) : VisionAnswer.Fail(error!, image.Length);
    }

    /// <summary>
    /// Shows the endpoint a picture and reads the answer — the only way to know whether it can see.
    /// </summary>
    public async Task<VisionProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var target = _target();
        if (target is null)
            return VisionProbeResult.Unknown(VisionTargetSelector.MissingReason);

        LastTargetName = target.Name;

        var (status, body, transportError) =
            await SendAsync(target, VisionProbe.SampleImage(), VisionProbe.Prompt, cancellationToken)
                .ConfigureAwait(false);

        // A timeout or a refused connection says nothing about whether the model can see, so it must
        // not be recorded as "cannot see".
        if (transportError is not null) return VisionProbeResult.Unknown(transportError);

        return VisionProbe.Classify(status, body);
    }

    private async Task<(int Status, string Body, string? TransportError)> SendAsync(
        VisionTarget target,
        byte[] image,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl);
            request.Content = VisionRequest.BuildContent(target, prompt, image);

            if (!string.IsNullOrWhiteSpace(target.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey.Trim());

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return ((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (0, "", "这一眼被取消了。");
        }
        catch (TaskCanceledException)
        {
            return (0, "", "视觉接口超时了（45 秒没有回应）。");
        }
        catch (HttpRequestException ex)
        {
            return (0, "", $"连不上视觉接口：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
