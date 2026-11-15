using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.Messaging;

public sealed record QQOfficialTokenResult(bool Ok, string Message, string? AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Minimal client for the QQ official bot API.
///
/// Only the token exchange is exercised here: POST appId/clientSecret to
/// <c>bots.qq.com/app/getAppAccessToken</c> and cache the result until it expires. The secret and
/// the token never appear in messages or logs this class produces.
///
/// Message sending follows the documented endpoints (<c>api.sgroup.qq.com/v2/...</c>) but has not
/// been run against a real bot — there are no credentials here to try it with.
/// </summary>
public sealed class QQOfficialClient
{
    private static readonly TimeSpan TokenLifetimeFallback = TimeSpan.FromMinutes(60);

    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;
    private readonly string _apiRoot;

    private string? _cachedToken;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    public QQOfficialClient(
        HttpClient? http = null,
        string tokenEndpoint = "https://bots.qq.com/app/getAppAccessToken",
        string apiRoot = "https://api.sgroup.qq.com")
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _tokenEndpoint = tokenEndpoint;
        _apiRoot = apiRoot;
    }

    public async Task<QQOfficialTokenResult> GetTokenAsync(
        string appId, string appSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
            return new(false, "未填写 AppID 或 AppSecret。", null, default);

        if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedUntil)
            return new(true, "已使用缓存的 access_token。", _cachedToken, _cachedUntil);

        try
        {
            var payload = JsonSerializer.Serialize(new { appId, clientSecret = appSecret });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_tokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, "AppID 或 AppSecret 无效（腾讯接口拒绝）。", null, default);

            if (!response.IsSuccessStatusCode)
                return new(false, $"腾讯接口返回 HTTP {(int)response.StatusCode}。", null, default);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.GetString() is not { Length: > 0 } token)
            {
                var code = root.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "未知";
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                return new(false, $"未取到 access_token（code={code}{(message is null ? "" : "，" + message)}）。", null, default);
            }

            var lifetime = root.TryGetProperty("expires_in", out var expiresElement) &&
                           int.TryParse(expiresElement.ToString(), out var seconds) && seconds > 60
                ? TimeSpan.FromSeconds(seconds - 60)
                : TokenLifetimeFallback;

            _cachedToken = token;
            _cachedUntil = DateTimeOffset.UtcNow + lifetime;
            return new(true, "access_token 获取成功。", token, _cachedUntil);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the type: an exception message can carry the request body or URL.
            return new(false, $"无法连接腾讯接口（{ex.GetType().Name}）。", null, default);
        }
    }

    /// <summary>
    /// Sends a plain-text message to a C2C (user) chat. Implemented from the documented shape but
    /// never executed against a live bot; treat it as unverified until a real account runs it.
    /// </summary>
    public async Task<(bool Ok, string Message)> SendDirectAsync(
        string accessToken, string userOpenId, string content, string? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(userOpenId))
            return (false, "缺少 access_token 或用户 openid。");

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                content,
                msg_type = 0,
                msg_id = replyToMessageId ?? Guid.NewGuid().ToString("N"),
                msg_seq = 1
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiRoot}/v2/users/{userOpenId}/messages")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"QQBot {accessToken}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, "已发送。");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, $"发送失败：HTTP {(int)response.StatusCode}，{Shorten(body)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"发送出错（{ex.GetType().Name}）。");
        }
    }

    private static string Shorten(string value) =>
        value.Length <= 120 ? value.Trim() : value[..120].Trim() + "…";
}

/// <summary>QQ 官方 Bot API 路线：AppID + AppSecret 直连，不依赖 OpenClaw 插件。</summary>
public sealed class QQOfficialAdapter : IMessagingAdapter
{
    private readonly Func<string?> _appId;
    private readonly Func<string?> _appSecret;
    private readonly QQOfficialClient _client;

    public QQOfficialAdapter(Func<string?> appId, Func<string?> appSecret, QQOfficialClient? client = null)
    {
        _appId = appId;
        _appSecret = appSecret;
        _client = client ?? new QQOfficialClient();
    }

    public MessagingPlatformInfo Info { get; } = new(
        PlatformKind.QQOfficial,
        "QQ（官方 API）",
        ImplementationState.Implemented,
        new PlatformCapabilities(DirectMessage: true, GroupChat: true, Media: false),
        "QQ 开放平台机器人：AppID + AppSecret（api.sgroup.qq.com）");

    public async Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var appId = _appId();
        var appSecret = _appSecret();

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
            return new(PlatformProbeState.NeedsConfiguration, "未填写 AppID 或 AppSecret。");

        var token = await _client.GetTokenAsync(appId, appSecret, cancellationToken).ConfigureAwait(false);
        if (!token.Ok) return new(PlatformProbeState.Failed, token.Message);

        return new(PlatformProbeState.Ready,
            $"凭据有效（access_token 有效期至 {token.ExpiresAt:HH:mm}）。注意：消息收发链路尚未接通，当前只验证了凭据。");
    }
}