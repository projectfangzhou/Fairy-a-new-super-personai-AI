using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.HomeAssistant;

/// <summary>
/// PKCE (RFC 7636) for Home Assistant's OAuth2 authorization-code flow.
///
/// The verifier never leaves this process; only its S256 challenge goes to the server. A
/// response that arrives without the matching <c>state</c> is rejected before any token
/// exchange, so another page cannot hand us a code for its own session.
/// </summary>
public static class HomeAssistantPkce
{
    public sealed record Pair(string Verifier, string Challenge);

    public static Pair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new Pair(verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Parses <c>code</c> and <c>state</c> out of a redirect URL, a bare query string, or an
    /// <c>error</c> reply. Returns false when the callback carries no recognizable result.
    /// </summary>
    public static bool TryReadCallback(string callback, out string? code, out string? state, out string? error)
    {
        code = null;
        state = null;
        error = null;

        if (string.IsNullOrWhiteSpace(callback)) return false;

        var queryStart = callback.IndexOf('?');
        var query = queryStart >= 0 ? callback[(queryStart + 1)..] : callback;
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0) query = query[..fragmentStart];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var key = pair[..separator];
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);

            switch (key)
            {
                case "code": code = value; break;
                case "state": state = value; break;
                case "error": error = value; break;
            }
        }

        return code is not null || error is not null;
    }

    public static string BuildAuthorizeUrl(Uri server, string clientId, string redirectUri, string state, string challenge)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var builder = new UriBuilder(new Uri(server, "auth/authorize"))
        {
            Query = string.Join('&',
                "response_type=code",
                "client_id=" + Uri.EscapeDataString(clientId),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "state=" + Uri.EscapeDataString(state),
                "code_challenge=" + Uri.EscapeDataString(challenge),
                "code_challenge_method=S256")
        };
        return builder.Uri.ToString();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The token endpoints. Only the tokens are exchanged here; nothing about the user or the
/// instance's contents flows through this class.
/// </summary>
public static class HomeAssistantOAuth
{
    public static async Task<HaTokenSet> ExchangeCodeAsync(
        HttpClient http, Uri server, string clientId, string code, string redirectUri, string verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        return await PostTokenAsync(http, server, form, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HaTokenSet> RefreshAsync(
        HttpClient http, Uri server, string clientId, string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        };
        return await PostTokenAsync(http, server, form, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HaTokenSet> PostTokenAsync(
        HttpClient http, Uri server, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(server, "auth/token"), content, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The body is a short OAuth error code; the form we sent contained the code and the
            // verifier, so it is never echoed back into a message or a log.
            var reason = body.Length > 200 ? body[..200] : body;
            throw new HaAuthenticationException($"换取令牌失败（HTTP {(int)response.StatusCode}）：{reason}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var access = root.TryGetProperty("access_token", out var accessElement) ? accessElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(access))
                throw new HaAuthenticationException("令牌响应里没有 access_token");

            var refresh = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
                ? seconds
                : 1800;

            return new HaTokenSet(access!, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
        catch (JsonException ex)
        {
            throw new HaAuthenticationException($"令牌响应不是合法 JSON：{ex.Message}");
        }
    }
}
