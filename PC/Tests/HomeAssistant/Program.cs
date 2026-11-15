using System.Net;
using System.Text;
using MyAiAssistant.Services;
using MyAiAssistant.Services.HomeAssistant;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-ha-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ---------------------------------------------------------------- PKCE

    // RFC 7636 appendix B: the published vector. If S256 is wrong, every real exchange fails.
    Assert(
        HomeAssistantPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") ==
        "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
        "RFC 7636 S256 test vector");

    {
        var pair = HomeAssistantPkce.Create();
        Assert(pair.Verifier.Length is >= 43 and <= 128, "verifier length within RFC bounds");
        Assert(pair.Challenge == HomeAssistantPkce.Challenge(pair.Verifier), "challenge derived from verifier");
        Assert(!pair.Verifier.Contains('=') && !pair.Verifier.Contains('+') && !pair.Verifier.Contains('/'),
            "verifier is base64url without padding");
        Assert(HomeAssistantPkce.Create().Verifier != pair.Verifier, "verifier is fresh each time");
    }

    {
        var url = HomeAssistantPkce.BuildAuthorizeUrl(
            new Uri("http://192.168.1.10:8123/"), "http://127.0.0.1:8137",
            "http://127.0.0.1:8137/callback", "state-abc", "challenge-xyz");

        Assert(url.StartsWith("http://192.168.1.10:8123/auth/authorize?", StringComparison.Ordinal), "authorize path");
        Assert(url.Contains("response_type=code", StringComparison.Ordinal), "authorize asks for a code");
        Assert(url.Contains("code_challenge_method=S256", StringComparison.Ordinal), "authorize uses S256");
        Assert(url.Contains("state=state-abc", StringComparison.Ordinal), "authorize carries state");
        Assert(url.Contains(Uri.EscapeDataString("http://127.0.0.1:8137/callback"), StringComparison.Ordinal),
            "redirect uri is escaped");
    }

    {
        Assert(HomeAssistantPkce.TryReadCallback(
                "http://127.0.0.1:8137/callback?code=abc&state=xyz", out var code, out var state, out _) &&
            code == "abc" && state == "xyz", "reads code and state from a full url");

        Assert(HomeAssistantPkce.TryReadCallback("code=abc&state=xyz", out var bareCode, out _, out _) && bareCode == "abc",
            "reads a bare query string");

        Assert(HomeAssistantPkce.TryReadCallback(
                "http://127.0.0.1:8137/callback?error=access_denied&state=xyz", out _, out _, out var error) &&
            error == "access_denied", "surfaces the error reply");

        Assert(!HomeAssistantPkce.TryReadCallback("http://127.0.0.1:8137/callback", out _, out _, out _),
            "a callback without a result is rejected");
        Assert(!HomeAssistantPkce.TryReadCallback("", out _, out _, out _), "empty callback is rejected");
    }

    // ---------------------------------------------------------------- server address

    {
        Assert(HomeAssistantClient.TryNormalizeServer("192.168.1.10:8123", out var bare, out _) &&
               bare.ToString() == "http://192.168.1.10:8123/", "bare host gains a scheme and a trailing slash");
        Assert(HomeAssistantClient.TryNormalizeServer("https://ha.example.com", out var https, out _) &&
               https.Scheme == Uri.UriSchemeHttps, "https is kept");
        Assert(!HomeAssistantClient.TryNormalizeServer("file:///c:/tmp", out _, out var schemeError) &&
               schemeError.Contains("http", StringComparison.Ordinal), "only http and https are accepted");
        Assert(!HomeAssistantClient.TryNormalizeServer("   ", out _, out _), "blank address is rejected");
    }

    // ---------------------------------------------------------------- REST client

    using (var handler = new StubHandler())
    using (var http = new HttpClient(handler))
    {
        var client = new HomeAssistantClient(http, new Uri("http://ha.local:8123/"))
        {
            TokenProvider = _ => Task.FromResult<string?>("tok-1"),
        };

        var entities = await client.GetStatesAsync();
        Assert(entities.Count == 4, "all entities parsed");

        var kitchen = entities.First(e => e.EntityId == "light.kitchen");
        Assert(kitchen.Domain == "light" && kitchen.State == "on" && kitchen.FriendlyName == "厨房灯",
            "entity fields parsed");
        Assert(kitchen.Unit is null, "missing unit stays null");

        var sensor = entities.First(e => e.EntityId == "sensor.temp");
        Assert(sensor.Unit == "°C", "unit parsed");
        Assert(sensor.FriendlyName == "sensor.temp", "missing friendly name falls back to the id");

        Assert(handler.Requests[0].Authorization == "Bearer tok-1", "bearer token is sent");

        var info = await client.GetApiInfoAsync();
        Assert(info.Message == "API running" && info.LocationName == "测试小屋" && info.Version == "2024.1",
            "api info parsed");

        await client.CallServiceAsync(new HaServiceCall("light", "turn_on", "light.kitchen", "{\"brightness\":128}"));
        var call = handler.Requests.Last();
        Assert(call.Url.EndsWith("/api/services/light/turn_on", StringComparison.Ordinal), "service path");
        Assert(call.Body!.Contains("\"entity_id\":\"light.kitchen\"", StringComparison.Ordinal), "entity id sent");
        Assert(call.Body!.Contains("\"brightness\":128", StringComparison.Ordinal), "extra data merged");
    }

    {
        using var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        using var http = new HttpClient(handler);
        var client = new HomeAssistantClient(http, new Uri("http://ha.local:8123/"))
        {
            TokenProvider = _ => Task.FromResult<string?>("expired"),
        };

        var unauthorized = false;
        try
        {
            await client.GetStatesAsync();
        }
        catch (HaAuthenticationException)
        {
            unauthorized = true;
        }
        Assert(unauthorized, "401 becomes an authentication failure");

        var noToken = new HomeAssistantClient(http, new Uri("http://ha.local:8123/"));
        var refused = false;
        try
        {
            await noToken.GetApiInfoAsync();
        }
        catch (HaAuthenticationException)
        {
            refused = true;
        }
        Assert(refused, "a missing token never reaches the network");
    }

    // ---------------------------------------------------------------- OAuth endpoints

    using (var handler = new StubHandler())
    using (var http = new HttpClient(handler))
    {
        var server = new Uri("http://ha.local:8123/");

        var tokens = await HomeAssistantOAuth.ExchangeCodeAsync(
            http, server, "client-1", "the-code", "http://127.0.0.1:8137/callback", "the-verifier");

        Assert(tokens.AccessToken == "access-1" && tokens.RefreshToken == "refresh-1", "tokens parsed");
        Assert(tokens.ExpiresAt > DateTimeOffset.UtcNow, "expiry is in the future");

        var exchangeBody = handler.Requests.Last().Body!;
        Assert(exchangeBody.Contains("grant_type=authorization_code", StringComparison.Ordinal), "exchange grant");
        Assert(exchangeBody.Contains("code_verifier=the-verifier", StringComparison.Ordinal), "verifier sent");
        Assert(exchangeBody.Contains("code=the-code", StringComparison.Ordinal), "code sent");

        Assert(!tokens.ToString().Contains("access-1", StringComparison.Ordinal), "token ToString is redacted");

        await HomeAssistantOAuth.RefreshAsync(http, server, "client-1", "refresh-1");
        var refreshBody = handler.Requests.Last().Body!;
        Assert(refreshBody.Contains("grant_type=refresh_token", StringComparison.Ordinal), "refresh grant");
        Assert(refreshBody.Contains("refresh_token=refresh-1", StringComparison.Ordinal), "refresh token sent");
        Assert(!refreshBody.Contains("code_verifier", StringComparison.Ordinal), "refresh never resends a verifier");
    }

    {
        using var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}"),
            },
        };
        using var http = new HttpClient(handler);
        var rejected = false;
        try
        {
            await HomeAssistantOAuth.ExchangeCodeAsync(http, new Uri("http://ha.local:8123/"),
                "client-1", "bad", "http://127.0.0.1:8137/callback", "verifier");
        }
        catch (HaAuthenticationException ex)
        {
            rejected = !ex.Message.Contains("verifier", StringComparison.Ordinal);
        }
        Assert(rejected, "a rejected exchange throws without echoing the verifier");
    }

    // ---------------------------------------------------------------- token storage

    {
        var store = new HomeAssistantTokenStore(Path.Combine(directory, "store-1"));
        Assert(store.Load() is null, "nothing stored yet");

        var tokens = new HaTokenSet("atok-1234", "rtok-5678", DateTimeOffset.UtcNow.AddMinutes(30));
        Assert(store.Save(tokens), "tokens saved");

        var raw = File.ReadAllText(store.FilePath);
        Assert(!raw.Contains("atok-1234", StringComparison.Ordinal), "access token is not plaintext on disk");
        Assert(!raw.Contains("rtok-5678", StringComparison.Ordinal), "refresh token is not plaintext on disk");

        var loaded = store.Load();
        Assert(loaded is not null && loaded.AccessToken == "atok-1234" && loaded.RefreshToken == "rtok-5678",
            "tokens read back unchanged");

        File.WriteAllText(store.FilePath, "not-a-valid-blob");
        Assert(store.Load() is null && store.LastError is not null, "a damaged file yields nothing, not an exception");

        Assert(store.Clear(), "clear succeeds");
        Assert(store.Load() is null, "clear removes the file");
    }

    // ---------------------------------------------------------------- risk classification

    Assert(HomeAssistantRisk.Classify(new HaServiceCall("light", "turn_on", "light.kitchen", null)) ==
           RiskCategory.ControlExternalDevice, "turning a light on is an external device action");
    Assert(HomeAssistantRisk.Classify(new HaServiceCall("homeassistant", "restart", null, null)) ==
           RiskCategory.PrivilegeEscalation, "restarting the instance is escalation");
    Assert(HomeAssistantRisk.Classify(new HaServiceCall("shell_command", "backup", null, null)) ==
           RiskCategory.PrivilegeEscalation, "shell_command is escalation");
    Assert(HomeAssistantRisk.Classify(new HaServiceCall("lock", "unlock", "lock.front", null)) ==
           RiskCategory.PrivilegeEscalation, "unlocking a door is escalation");

    // ---------------------------------------------------------------- control: writes need a yes

    async Task<(HomeAssistantControl Control, StubHandler Handler, HomeAssistantTokenStore Store, ScriptedApprover Approver)>
        ConnectedAsync(string label)
    {
        var handler = new StubHandler();
        var store = new HomeAssistantTokenStore(Path.Combine(directory, label));
        var approver = new ScriptedApprover();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, label)));
        var control = new HomeAssistantControl(
            new HttpClient(handler), store, gate, approver,
            new HomeAssistantSettings("http://ha.local:8123/", "http://127.0.0.1:8137", "http://127.0.0.1:8137/callback"));

        var signIn = await control.UseAccessTokenAsync("token-abc");
        Assert(signIn.Ok, $"{label}: token connection succeeds");
        return (control, handler, store, approver);
    }

    {
        var (control, handler, _, approver) = await ConnectedAsync("case-refuse");
        approver.Answer = false;

        var view = control.Read();
        Assert(view.Connected, "refuse case: reports connected");
        Assert(view.Scenes.Count == 1 && view.Automations.Count == 1, "refuse case: scenes and automations listed");
        Assert(view.Entities.Any(e => e.EntityId == "light.kitchen"), "refuse case: controllable entities listed");
        Assert(view.EntityTotal == 2, "refuse case: entity total excludes scenes and automations");

        var before = handler.Requests.Count;
        var denied = await control.CallServiceAsync("light", "turn_on", "light.kitchen", null);
        Assert(!denied.Ok, "refuse case: call not executed");
        Assert(approver.Asked == 1, "refuse case: the user was asked exactly once");
        Assert(handler.Requests.Count == before, "refuse case: no service request left the machine");
    }

    {
        var (control, handler, _, approver) = await ConnectedAsync("case-approve");
        approver.Answer = true;

        var ok = await control.CallServiceAsync("light", "turn_on", "light.kitchen", "{\"brightness\":128}");
        Assert(ok.Ok, "approve case: call executed");
        Assert(approver.Asked == 1, "approve case: asked once");
        var call = handler.Requests.Last();
        Assert(call.Url.EndsWith("/api/services/light/turn_on", StringComparison.Ordinal), "approve case: right service");
        Assert(call.Body!.Contains("\"brightness\":128", StringComparison.Ordinal), "approve case: data forwarded");
    }

    {
        var (control, handler, store, approver) = await ConnectedAsync("case-validate");
        approver.Answer = true;

        var before = handler.Requests.Count;
        var badJson = await control.CallServiceAsync("light", "turn_on", "light.kitchen", "not json");
        Assert(!badJson.Ok, "invalid extra data is rejected");
        Assert(approver.Asked == 0, "invalid extra data never reaches the user");
        Assert(handler.Requests.Count == before, "invalid extra data never reaches the network");

        var signedOut = control.SignOut();
        Assert(signedOut.Ok, "sign out reports success");
        Assert(store.Load() is null, "sign out clears the stored token");

        var afterSignOut = await control.CallServiceAsync("light", "turn_on", "light.kitchen", null);
        Assert(!afterSignOut.Ok && afterSignOut.Message.Contains("连接", StringComparison.Ordinal),
            "no token means no call");
    }

    // ---------------------------------------------------------------- control: authorization flow

    {
        var handler = new StubHandler();
        var store = new HomeAssistantTokenStore(Path.Combine(directory, "case-auth"));
        var approver = new ScriptedApprover();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "case-auth")));
        var control = new HomeAssistantControl(
            new HttpClient(handler), store, gate, approver,
            new HomeAssistantSettings("http://ha.local:8123/", "http://127.0.0.1:8137", "http://127.0.0.1:8137/callback"));

        var begin = control.BeginAuthorization();
        Assert(begin.Ok && begin.AuthorizeUrl is not null, "authorization link generated");
        Assert(control.Read().PendingAuthorizeUrl is not null, "pending link is visible to the dashboard");

        var authorizeState = ReadQuery(begin.AuthorizeUrl!, "state");
        Assert(!string.IsNullOrEmpty(authorizeState), "authorize link carries a state");

        var requestsBeforeMismatch = handler.Requests.Count;
        var mismatch = await control.CompleteAuthorizationAsync(
            $"http://127.0.0.1:8137/callback?code=abc&state=wrong-{authorizeState}");
        Assert(!mismatch.Ok, "mismatched state is rejected");
        Assert(handler.Requests.Count == requestsBeforeMismatch, "mismatched state never reaches the token endpoint");

        var completed = await control.CompleteAuthorizationAsync(
            $"http://127.0.0.1:8137/callback?code=good-code&state={authorizeState}");
        Assert(completed.Ok, "authorization completes");

        var stored = store.Load();
        Assert(stored is not null && stored.AccessToken == "access-1", "exchanged tokens are stored");

        var view = control.Read();
        Assert(view.Connected && view.LocationName == "测试小屋", "connected after authorizing");
        Assert(view.PendingAuthorizeUrl is null, "the pending link is cleared once used");

        var replay = await control.CompleteAuthorizationAsync(
            $"http://127.0.0.1:8137/callback?code=good-code&state={authorizeState}");
        Assert(!replay.Ok, "the same callback cannot be replayed");

        var switched = control.ApplySettings("http://other.local:8123", null, null);
        Assert(switched.Ok, "server address can be changed");
        Assert(store.Load() is null, "changing the server drops the old instance's token");
        Assert(!control.Read().Connected, "changing the server disconnects");

        var badAddress = control.ApplySettings("ftp://ha.local", null, null);
        Assert(!badAddress.Ok, "a non-http address is refused");
    }

    // ---------------------------------------------------------------- control: failures surface

    {
        var handler = new StubHandler();
        var store = new HomeAssistantTokenStore(Path.Combine(directory, "case-fail"));
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "case-fail")));
        var control = new HomeAssistantControl(
            new HttpClient(handler), store, gate, new ScriptedApprover(),
            new HomeAssistantSettings("http://ha.local:8123/", "http://127.0.0.1:8137", "http://127.0.0.1:8137/callback"));

        var notConnected = await control.ConnectAsync();
        Assert(!notConnected.Ok, "connect without a token fails");

        await control.UseAccessTokenAsync("token-abc");
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var expired = await control.ConnectAsync();
        Assert(!expired.Ok && expired.Message.Contains("令牌", StringComparison.Ordinal), "expired token is reported");
        Assert(control.Read().Error is not null, "the error is visible to the dashboard");

        handler.Responder = (_, _) => throw new HttpRequestException("no route to host");
        var offline = await control.ConnectAsync();
        Assert(!offline.Ok && offline.Message.Contains("连不上", StringComparison.Ordinal), "unreachable server is reported");
    }

    // ---------------------------------------------------------------- nothing leaks into the view

    {
        var (control, _, _, _) = await ConnectedAsync("case-leak");
        var serialized = System.Text.Json.JsonSerializer.Serialize(control.Read());
        Assert(!serialized.Contains("token-abc", StringComparison.Ordinal), "the view never carries the token");
        Assert(!serialized.Contains("access-1", StringComparison.Ordinal), "the view never carries an access token");
    }

    Console.WriteLine($"All {checks} home assistant checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}

static string? ReadQuery(string url, string key)
{
    var queryStart = url.IndexOf('?');
    if (queryStart < 0) return null;
    foreach (var pair in url[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = pair.IndexOf('=');
        if (separator <= 0) continue;
        if (pair[..separator] == key) return Uri.UnescapeDataString(pair[(separator + 1)..]);
    }
    return null;
}

internal sealed class ScriptedApprover : IHomeAssistantApprover
{
    public bool Answer { get; set; }

    public int Asked { get; private set; }

    public Task<bool> AskAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}

/// <summary>A Home Assistant that exists only in this process: known responses, recorded requests.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private const string StatesJson = """
        [
          {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"厨房灯"},"last_changed":"2026-10-01T10:00:00+00:00"},
          {"entity_id":"scene.movie","state":"unknown","attributes":{"friendly_name":"观影模式"},"last_changed":"2026-10-01T10:00:00+00:00"},
          {"entity_id":"automation.wake","state":"on","attributes":{"friendly_name":"早上起床"},"last_changed":"2026-10-01T10:00:00+00:00"},
          {"entity_id":"sensor.temp","state":"21.5","attributes":{"unit_of_measurement":"°C"},"last_changed":"2026-10-01T10:00:00+00:00"}
        ]
        """;

    public List<(string Method, string Url, string? Body, string? Authorization)> Requests { get; } = new();

    public Func<HttpRequestMessage, string?, HttpResponseMessage>? Responder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var authorization = request.Headers.TryGetValues("Authorization", out var values)
            ? string.Join(",", values)
            : null;

        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body, authorization));

        if (Responder is not null) return Responder(request, body);

        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/token", StringComparison.Ordinal))
            return Json("{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":1800,\"token_type\":\"Bearer\"}");
        if (path.EndsWith("/api/states", StringComparison.Ordinal))
            return Json(StatesJson);
        if (path.EndsWith("/api/", StringComparison.Ordinal) || path.EndsWith("/api", StringComparison.Ordinal))
            return Json("{\"message\":\"API running\",\"location_name\":\"测试小屋\",\"version\":\"2024.1\"}");
        if (path.Contains("/api/services/", StringComparison.Ordinal))
            return Json("[]");

        return Json("{\"message\":\"not found\"}", HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
