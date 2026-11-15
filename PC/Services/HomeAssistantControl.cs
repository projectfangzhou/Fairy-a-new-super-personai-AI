using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.HomeAssistant;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services;

/// <summary>
/// Wires the Home Assistant client into the app: one place that knows the instance address, the
/// token and the cached state, and one door that every service call must pass before it leaves
/// the machine.
///
/// Reads (states, scenes, automations) are cached and need no confirmation. Writes always go
/// through <see cref="ApprovalGate"/> with an approver that surfaces in the shared pending list,
/// so "turn on the living-room light" is a question the user answers, not something an agent can
/// do because it happens to hold a token.
/// </summary>
public sealed class HomeAssistantControl : IHomeAssistantControl, IDisposable
{
    private const int EntityCapShown = 500;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly HomeAssistantTokenStore _store;
    private readonly ApprovalGate _approval;
    private readonly IHomeAssistantApprover _approver;
    private readonly Action<HomeAssistantSettings>? _persist;
    private readonly object _sync = new();

    private HomeAssistantSettings _settings;
    private PendingAuthorization? _pending;
    private HaTokenSet? _tokens;
    private HomeAssistantClient? _client;
    private HaApiInfo? _info;
    private IReadOnlyList<HaEntity> _entities = Array.Empty<HaEntity>();
    private DateTimeOffset? _refreshed;
    private string? _error;

    public HomeAssistantControl(
        HttpClient http,
        HomeAssistantTokenStore store,
        ApprovalGate approval,
        IHomeAssistantApprover approver,
        HomeAssistantSettings settings,
        Action<HomeAssistantSettings>? persist = null,
        bool ownsHttpClient = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _approver = approver ?? throw new ArgumentNullException(nameof(approver));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persist = persist;
        _ownsHttpClient = ownsHttpClient;

        _tokens = _store.Load();
        if (_store.LastError is { } loadError) _error = loadError;
    }

    /// <summary>How long a started authorization stays usable. HA's own code expires; ours does too.</summary>
    public TimeSpan PendingLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public HomeAssistantView Read()
    {
        lock (_sync)
        {
            var scenes = _entities.Where(e => e.IsScene)
                .Select(e => new HaSceneView(e.EntityId, e.FriendlyName)).ToArray();
            var automations = _entities.Where(e => e.IsAutomation)
                .Select(e => new HaAutomationView(e.EntityId, e.FriendlyName, e.State)).ToArray();

            var others = _entities.Where(e => !e.IsScene && !e.IsAutomation).ToArray();
            var shown = others.Take(EntityCapShown)
                .Select(e => new HaEntityView(e.EntityId, e.Domain, e.FriendlyName, e.State, e.Unit)).ToArray();

            return new HomeAssistantView(
                Configured: IsConfigured(out _, out _),
                Connected: _tokens is not null && _refreshed is not null,
                ServerUrl: _settings.ServerUrl,
                LocationName: _info?.LocationName,
                Version: _info?.Version,
                Error: _error,
                PendingAuthorizeUrl: _pending is { } pending && !IsPendingExpired(pending) ? pending.AuthorizeUrl : null,
                RedirectUri: _settings.RedirectUri,
                LastRefreshed: _refreshed,
                EntityTotal: others.Length,
                Entities: shown,
                Scenes: scenes,
                Automations: automations);
        }
    }

    public HomeAssistantActionResult ApplySettings(string serverUrl, string? clientId, string? redirectUri)
    {
        if (!HomeAssistantClient.TryNormalizeServer(serverUrl, out var server, out var error))
            return new HomeAssistantActionResult(false, error);

        var previousServer = _settings.ServerUrl;

        lock (_sync)
        {
            _settings = new HomeAssistantSettings(
                server.ToString(),
                string.IsNullOrWhiteSpace(clientId) ? _settings.ClientId : clientId.Trim(),
                string.IsNullOrWhiteSpace(redirectUri) ? _settings.RedirectUri : redirectUri.Trim());

            if (!string.IsNullOrWhiteSpace(previousServer) &&
                !string.Equals(previousServer, _settings.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Tokens belong to one instance. Keeping them across an address change would mean
                // sending the old server's credential to whatever now answers at the new address.
                _tokens = null;
                _client = null;
                _info = null;
                _entities = Array.Empty<HaEntity>();
                _refreshed = null;
                _store.Clear();
                _error = null;
            }
        }

        _persist?.Invoke(_settings);
        return new HomeAssistantActionResult(true, $"已保存服务器地址 {_settings.ServerUrl}，下一步是授权");
    }

    public HomeAssistantActionResult BeginAuthorization()
    {
        if (!IsConfigured(out var server, out var configuredError))
            return new HomeAssistantActionResult(false, configuredError);

        var pkce = HomeAssistantPkce.Create();
        var state = HomeAssistantPkce.CreateState();
        var authorizeUrl = HomeAssistantPkce.BuildAuthorizeUrl(
            server, _settings.ClientId, _settings.RedirectUri, state, pkce.Challenge);

        lock (_sync)
        {
            _pending = new PendingAuthorization(pkce.Verifier, state, DateTimeOffset.UtcNow, authorizeUrl);
            _error = null;
        }

        return new HomeAssistantActionResult(true,
            "已生成授权链接。在浏览器里打开并同意后，把浏览器地址栏里的整条回调地址粘贴回来。", authorizeUrl);
    }

    public async Task<HomeAssistantActionResult> CompleteAuthorizationAsync(
        string callback, CancellationToken cancellationToken = default)
    {
        PendingAuthorization? pending;
        Uri server;
        lock (_sync) pending = _pending;

        if (pending is null)
            return new HomeAssistantActionResult(false, "还没有发起授权，请先生成授权链接");
        if (IsPendingExpired(pending))
            return new HomeAssistantActionResult(false, "授权会话已过期，请重新生成授权链接");
        if (!IsConfigured(out server, out var configuredError))
            return new HomeAssistantActionResult(false, configuredError);

        if (!HomeAssistantPkce.TryReadCallback(callback, out var code, out var state, out var callbackError))
            return new HomeAssistantActionResult(false, "这段内容里没有授权码，请粘贴浏览器地址栏里的完整地址");

        if (!string.IsNullOrWhiteSpace(callbackError))
            return new HomeAssistantActionResult(false, $"Home Assistant 拒绝了授权：{callbackError}");

        if (!StateMatches(state, pending.State))
            return new HomeAssistantActionResult(false, "回调里的 state 与本次授权不符，已拒绝（可能是别的会话的回调）");

        // One shot: a code that was already exchanged must not be exchangeable again by replaying
        // the paste.
        lock (_sync) _pending = null;

        try
        {
            var tokens = await HomeAssistantOAuth.ExchangeCodeAsync(
                _http, server, _settings.ClientId, code!, _settings.RedirectUri, pending.Verifier, cancellationToken)
                .ConfigureAwait(false);

            if (!_store.Save(tokens))
            {
                lock (_sync) _error = _store.LastError;
                return new HomeAssistantActionResult(false, _store.LastError ?? "令牌无法保存");
            }

            lock (_sync)
            {
                _tokens = tokens;
                _error = null;
                _client = BuildClient(server);
            }

            var connected = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return connected.Ok
                ? new HomeAssistantActionResult(true, "授权成功，" + connected.Message)
                : new HomeAssistantActionResult(false, "授权成功但读取状态失败：" + connected.Message);
        }
        catch (HaAuthenticationException ex)
        {
            return new HomeAssistantActionResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new HomeAssistantActionResult(false, $"连接 Home Assistant 失败：{ex.Message}");
        }
    }

    public async Task<HomeAssistantActionResult> UseAccessTokenAsync(
        string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new HomeAssistantActionResult(false, "请填写访问令牌");
        if (!IsConfigured(out var server, out var configuredError))
            return new HomeAssistantActionResult(false, configuredError);

        var trimmed = token.Trim();

        // A long-lived token has no expiry and no refresh token; ExpiresAt is far enough out that
        // IsExpired never fires.
        var tokens = new HaTokenSet(trimmed, null, DateTimeOffset.MaxValue);
        if (!_store.Save(tokens))
            return new HomeAssistantActionResult(false, _store.LastError ?? "令牌无法保存");

        lock (_sync)
        {
            _tokens = tokens;
            _client = BuildClient(server);
            _error = null;
        }

        var connected = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return connected.Ok
            ? new HomeAssistantActionResult(true, "已使用访问令牌连接，" + connected.Message)
            : new HomeAssistantActionResult(false, "令牌已保存但读取状态失败：" + connected.Message);
    }

    public async Task<HomeAssistantActionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        HomeAssistantClient? client;
        lock (_sync) client = _client;

        if (client is null)
            return new HomeAssistantActionResult(false, "还没有可用的令牌，请先授权");

        try
        {
            var info = await client.GetApiInfoAsync(cancellationToken).ConfigureAwait(false);
            var entities = await client.GetStatesAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _info = info;
                _entities = entities;
                _refreshed = DateTimeOffset.UtcNow;
                _error = null;
            }

            var name = string.IsNullOrWhiteSpace(info.LocationName) ? "Home Assistant" : info.LocationName!;
            return new HomeAssistantActionResult(true, $"已连接 {name}，读到 {entities.Count} 个实体");
        }
        catch (HaAuthenticationException ex)
        {
            lock (_sync) _error = ex.Message + "；请重新授权";
            return new HomeAssistantActionResult(false, ex.Message);
        }
        catch (HaRequestException ex)
        {
            lock (_sync) _error = ex.Message;
            return new HomeAssistantActionResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = $"连不上 {client.Server}：{ex.Message}";
            lock (_sync) _error = message;
            return new HomeAssistantActionResult(false, message);
        }
    }

    public HomeAssistantActionResult SignOut()
    {
        lock (_sync)
        {
            _tokens = null;
            _client = null;
            _info = null;
            _entities = Array.Empty<HaEntity>();
            _refreshed = null;
            _pending = null;
            _error = null;
            _store.Clear();
        }

        return new HomeAssistantActionResult(true,
            "已在本机清除令牌。要在服务端也作废，请在 Home Assistant 个人资料里删除这个授权。");
    }

    public async Task<HomeAssistantActionResult> CallServiceAsync(
        string domain, string service, string? entityId, string? dataJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
            return new HomeAssistantActionResult(false, "必须指定 domain 与 service");
        if (!string.IsNullOrWhiteSpace(dataJson) && !IsJsonObject(dataJson))
            return new HomeAssistantActionResult(false, "附加数据必须是 JSON 对象，例如 {\"brightness\":128}");

        HomeAssistantClient? client;
        lock (_sync) client = _client;

        if (client is null || _tokens is null)
            return new HomeAssistantActionResult(false, "还没有连接到 Home Assistant");

        var call = new HaServiceCall(domain, service, entityId, dataJson);
        var request = new ActionRequest(
            $"ha.service.{domain}.{service}",
            HomeAssistantRisk.Classify(call),
            HomeAssistantRisk.Describe(call),
            call.Target);

        ApprovalResult approval;
        try
        {
            approval = await _approval.AuthorizeAsync(request, cancellationToken,
                (pending, token) => _approver.AskAsync(pending, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new HomeAssistantActionResult(false, "已取消");
        }

        if (!approval.IsAllowed)
            return new HomeAssistantActionResult(false, $"未执行：{approval.Reason}");

        try
        {
            await client.CallServiceAsync(call, cancellationToken).ConfigureAwait(false);
            return new HomeAssistantActionResult(true, $"{HomeAssistantRisk.Describe(call)} 已执行");
        }
        catch (HaAuthenticationException ex)
        {
            return new HomeAssistantActionResult(false, ex.Message);
        }
        catch (HaRequestException ex)
        {
            return new HomeAssistantActionResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new HomeAssistantActionResult(false, $"连线出错：{ex.Message}");
        }
    }

    private HomeAssistantClient BuildClient(Uri server)
    {
        var client = new HomeAssistantClient(_http, server) { TokenProvider = SupplyTokenAsync };
        return client;
    }

    private async Task<string?> SupplyTokenAsync(CancellationToken cancellationToken)
    {
        HaTokenSet? tokens;
        Uri server;
        lock (_sync) tokens = _tokens;

        if (tokens is null) return null;
        if (!tokens.IsExpired(DateTimeOffset.UtcNow) || tokens.RefreshToken is null)
            return tokens.AccessToken;

        if (!IsConfigured(out server, out _)) return tokens.AccessToken;

        try
        {
            var refreshed = await HomeAssistantOAuth.RefreshAsync(
                _http, server, _settings.ClientId, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);

            lock (_sync) _tokens = refreshed;
            _store.Save(refreshed);
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            // Keep the old token: the call will fail with 401 and the user is told to re-authorize.
            lock (_sync) _error = $"令牌刷新失败：{ex.Message}";
            return tokens.AccessToken;
        }
    }

    private bool IsConfigured(out Uri server, out string error)
    {
        server = null!;
        error = "";
        var url = _settings.ServerUrl;
        return HomeAssistantClient.TryNormalizeServer(url, out server, out error);
    }

    private bool IsPendingExpired(PendingAuthorization pending) =>
        DateTimeOffset.UtcNow - pending.CreatedAt > PendingLifetime;

    private static bool StateMatches(string? provided, string expected)
    {
        if (provided is null) return false;
        var left = Encoding.UTF8.GetBytes(provided);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private sealed record PendingAuthorization(string Verifier, string State, DateTimeOffset CreatedAt, string AuthorizeUrl);
}
