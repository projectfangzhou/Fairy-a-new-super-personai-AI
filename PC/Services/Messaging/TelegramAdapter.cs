using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// Telegram over the official Bot API. The probe calls <c>getMe</c>, which is the cheapest call
/// that proves a token is valid — no message is sent, and the token never appears in any message
/// or log this class produces.
/// </summary>
public sealed class TelegramAdapter : IMessagingAdapter
{
    private const string ApiRoot = "https://api.telegram.org/bot";

    private readonly string _token;
    private readonly HttpClient _http;
    private readonly Func<string> _tokenProvider;

    public TelegramAdapter(string? token, HttpClient? http = null, Func<string>? tokenProvider = null)
    {
        _token = token ?? "";
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _tokenProvider = tokenProvider ?? (() => _token);
    }

    public MessagingPlatformInfo Info { get; } = new(
        PlatformKind.Telegram,
        "Telegram",
        ImplementationState.Implemented,
        new PlatformCapabilities(DirectMessage: true, GroupChat: true, Media: true),
        "BotFather 申请 Bot Token");

    public async Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var token = (_tokenProvider() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
            return new(PlatformProbeState.NeedsConfiguration, "未填写 Bot Token。请先到 @BotFather 创建机器人并取得 Token。");

        try
        {
            using var response = await _http
                .GetAsync($"{ApiRoot}{token}/getMe", cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                return new(PlatformProbeState.Failed, "Token 无效或已被撤销。");

            if (!response.IsSuccessStatusCode)
                return new(PlatformProbeState.Failed, $"Telegram 返回 HTTP {(int)response.StatusCode}。");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return new(PlatformProbeState.Failed, "Telegram 未返回成功结果。");

            var username = root.TryGetProperty("result", out var result) &&
                           result.TryGetProperty("username", out var name)
                ? name.GetString()
                : null;

            return new(PlatformProbeState.Ready,
                username is null ? "Telegram Bot 可用。" : $"Telegram Bot 可用：@{username}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type: the message can contain the request URL, which has the token.
            return new(PlatformProbeState.Failed, $"无法连接 Telegram（{ex.GetType().Name}）。");
        }
    }
}