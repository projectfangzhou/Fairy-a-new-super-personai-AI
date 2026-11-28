using System.Net;
using System.Net.Sockets;
using System.Text;
using MyAiAssistant.Models;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Bots;
using MyAiAssistant.Services.Localization;
using MyAiAssistant.Services.Messaging;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

// --- 1. Registry honesty -------------------------------------------------
{
    var registry = new MessagingPlatformRegistry(new IMessagingAdapter[]
    {
        new TelegramAdapter(null),
        new QQAdapter(new QQBotIntegration(new FakeCli(), new[] { MissingPath() })),
        new QQOfficialAdapter(() => null, () => null),
        new WebChatAdapter()
    });

    Assert(registry.Platforms.Count == 17, "seven platforms listed");
    var implemented = registry.Platforms.Where(p => p.State == ImplementationState.Implemented)
        .Select(p => p.Kind).OrderBy(k => k).ToArray();
    Assert(implemented.SequenceEqual(new[]
            { PlatformKind.Telegram, PlatformKind.QQ, PlatformKind.QQOfficial, PlatformKind.WebChat }),
        "four platforms are implemented");

    foreach (var planned in registry.Platforms.Where(p => p.State == ImplementationState.Planned))
    {
        var probe = await registry.ProbeAsync(planned.Kind);
        Assert(probe.State == PlatformProbeState.Unavailable, $"{planned.Kind} probes as unavailable");
        Assert(!probe.IsReady, $"{planned.Kind} never reports ready");
        Assert(!string.IsNullOrWhiteSpace(planned.Requirement), $"{planned.Kind} names its requirement");
    }

    // Group chat is a hard requirement: the declared platforms must state whether they have it.
    Assert(registry.Platforms.First(p => p.Kind == PlatformKind.QQ).Capabilities.GroupChat, "QQ declares group chat");
    Assert(registry.Platforms.First(p => p.Kind == PlatformKind.Telegram).Capabilities.GroupChat, "Telegram declares group chat");
    Assert(!registry.Platforms.First(p => p.Kind == PlatformKind.WeChat).Capabilities.GroupChat,
        "personal WeChat is declared without group chat");
}

// --- 2. Telegram adapter -------------------------------------------------
{
    var noToken = await new TelegramAdapter("").ProbeAsync();
    Assert(noToken.State == PlatformProbeState.NeedsConfiguration, "missing token needs configuration");

    var ok = await new TelegramAdapter("123:abc", HttpClientWith(HttpStatusCode.OK,
        "{\"ok\":true,\"result\":{\"username\":\"fairy_bot\"}}")).ProbeAsync();
    Assert(ok.State == PlatformProbeState.Ready && ok.Message.Contains("@fairy_bot", StringComparison.Ordinal),
        "valid token reports the bot handle");

    var denied = await new TelegramAdapter("bad", HttpClientWith(HttpStatusCode.Unauthorized, "{}")).ProbeAsync();
    Assert(denied.State == PlatformProbeState.Failed, "invalid token fails");
    Assert(!denied.Message.Contains("bad", StringComparison.Ordinal), "token never echoed in the message");

    var error = await new TelegramAdapter("t", HttpClientWith(HttpStatusCode.InternalServerError, "{}")).ProbeAsync();
    Assert(error.State == PlatformProbeState.Failed, "server error fails");
}

// --- 3. QQ adapter mapping ----------------------------------------------
{
    var missing = new QQBotIntegration(new FakeCli(), new[] { MissingPath() });
    var probe = await new QQAdapter(missing).ProbeAsync();
    Assert(probe.State == PlatformProbeState.NeedsConfiguration, "QQ without OpenClaw asks for configuration");

    var cliPath = TempFile();
    var readyCli = new FakeCli
    {
        Respond = args => args == "--version"
            ? new CliResult(true, 0, "1.0.0")
            : new CliResult(true, 0, "@tencent-connect/openclaw-qqbot enabled")
    };
    var ready = await new QQAdapter(new QQBotIntegration(readyCli, new[] { cliPath })).ProbeAsync();
    Assert(ready.State == PlatformProbeState.Ready, "QQ ready when the plugin is present");
    Assert(ready.Message.Contains("群聊", StringComparison.Ordinal), "QQ readiness explains group chat");
}

// --- 4. WebChat adapter (loopback websocket) ----------------------------
{
    Assert(MessagingAccountService.RequiredFields(PlatformKind.WebChat).Count == 0,
        "web chat needs no credentials");
    Assert(MessagingAccountService.IsImplemented(PlatformKind.WebChat), "web chat is addable");
    Assert(!new WebChatAdapter().Info.Capabilities.GroupChat, "web chat declares no group chat");

    // A stopped server must never report ready: a throwaway socket test would prove nothing about
    // whether chat works, so the probe names the real state instead.
    var stopped = await new WebChatAdapter().ProbeAsync();
    Assert(stopped.State == PlatformProbeState.NeedsConfiguration, "stopped server asks to be started");
    Assert(!stopped.IsReady, "stopped server never reports ready");
    Assert(stopped.Message.Contains("ws://127.0.0.1:", StringComparison.Ordinal), "the hint names the local url");

    // A running server answers a ping round trip, which is what readiness has to mean now.
    var port = FreePort();
    using var endpoint = new WebChatEndpoint(new FakeResponder());
    endpoint.Start(port);
    var live = await new WebChatAdapter(endpoint).ProbeAsync();
    Assert(live.State == PlatformProbeState.Ready, "running server passes a real ping round trip");
    Assert(live.Message.Contains("ws://127.0.0.1:", StringComparison.Ordinal), "readiness names the local url");

    // The probe's own connection must not be reported as a client: the number is user-facing, and
    // "1 个连接" on an idle server would be a lie the user cannot check.
    Assert(!live.Message.Contains("个连接", StringComparison.Ordinal),
        "an idle server reports no connections: " + live.Message);
    await WaitAsync(() => endpoint.ClientCount == 0, 2000);
    Assert(endpoint.ClientCount == 0, "the probe leaves no connection behind");

    // Second start of the same port must fail: only one server can own it.
    var control = new WebChatControl(endpoint);
    Assert(control.Status().Running, "control reports the server running");
    endpoint.Stop();
    Assert(!control.Status().Running, "control reports the server stopped");
    Assert(control.Start().Ok, "control starts a stopped server");
    Assert(endpoint.Port == port, "restart reuses the same port, proving it was released");

    // A taken port must fail loudly rather than pretend.
    var otherPort = FreePort();
    using var squatter = new WebChatEndpoint(new FakeResponder());
    squatter.Start(otherPort);
    var blocked = new WebChatControl(new WebChatEndpoint(new FakeResponder()), otherPort).Start();
    Assert(!blocked.Ok, "occupied port fails to start");
    Assert(blocked.Message.Contains("占用", StringComparison.Ordinal), "the failure names the cause");

    // The account path is what the UI actually calls, so prove it end to end rather than
    // only the adapter in isolation.
    var config = new AppConfig();
    var store = new MessagingAccountStore(() => config, saved => config = saved);
    var added = store.Add(PlatformKind.WebChat, "本机网页", null);
    Assert(added.Ok, "web chat account adds with no credentials at all");
    Assert(added.Id is not null, "added account comes back with an id");

    // Adding is not the same as turning the capability on: the flag decides whether a loopback
    // port opens at launch, so it must start off and be flipped deliberately.
    Assert(store.Find(added.Id!)!.Enabled == false, "a new account starts disabled");
    Assert(store.SetEnabled(added.Id!, true), "enabling reports a change");
    Assert(store.Find(added.Id!)!.Enabled, "the account is enabled after enabling");
    Assert(!store.SetEnabled(added.Id!, true), "enabling again reports no change");
    Assert(!store.SetEnabled("no-such-id", true), "an unknown account reports no change");
    Assert(store.SetEnabled(added.Id!, false), "disabling reports a change");

    var viaService = await new MessagingAccountService(store, webChatEndpoint: endpoint).ProbeAsync(added.Id!);
    Assert(viaService.State == PlatformProbeState.Ready, "account probe path reports ready when the server runs");

    var viaServiceStopped = await new MessagingAccountService(store).ProbeAsync(added.Id!);
    Assert(viaServiceStopped.State == PlatformProbeState.NeedsConfiguration,
        "account probe reports the server as not started");
}

// --- 4d. WebChat 启动规则（安全决策，必须有测试） ----------------------
{
    var config = new AppConfig();
    var store = new MessagingAccountStore(() => config, saved => config = saved);

    // Nothing configured: the port must stay closed.
    Assert(!WebChatLaunch.ShouldStart(store.List()), "nothing configured means no server");

    var account = store.Add(PlatformKind.WebChat, "本机网页", null);
    Assert(!WebChatLaunch.ShouldStart(store.List()), "an added but disabled account does not start the server");

    store.SetEnabled(account.Id!, true);
    Assert(WebChatLaunch.ShouldStart(store.List()), "an enabled WebChat account starts it");

    store.SetEnabled(account.Id!, false);
    Assert(!WebChatLaunch.ShouldStart(store.List()), "disabling it stops it from starting");

    // Another platform being enabled must not drag the WebChat server up with it.
    var telegram = store.Add(PlatformKind.Telegram, "报信", new Dictionary<string, string> { ["token"] = "x" });
    Assert(telegram.Ok, "a second account of another platform can be added");
    store.SetEnabled(telegram.Id!, true);
    Assert(!WebChatLaunch.ShouldStart(store.List()), "an enabled Telegram account does not start WebChat");

    // The action half: it starts only when the rule allows it, and reports what the control said.
    var control = new RecordingWebChatControl();
    var blocked = WebChatLaunch.StartIfEnabled(store, control);
    Assert(!blocked.Started && control.StartCalls == 0, "no start is attempted while the rule says no");
    Assert(blocked.Message.Contains("端口保持关闭", StringComparison.Ordinal), "the message explains why nothing started");

    store.SetEnabled(account.Id!, true);
    var started = WebChatLaunch.StartIfEnabled(store, control);
    Assert(started.Started && control.StartCalls == 1, "an enabled account starts the server once");

    control.RefuseStart = true;
    store.SetEnabled(account.Id!, false);
    store.SetEnabled(account.Id!, true);
    var refused = WebChatLaunch.StartIfEnabled(store, control);
    Assert(!refused.Started && refused.Message.Contains("占用", StringComparison.Ordinal),
        "a refused start is reported as not started");
}

// --- 4b. WebChat protocol (pure) ----------------------------------------
{
    Assert(WebChatProtocol.Parse("{\"type\":\"ping\"}").Kind == WebChatMessageKind.Ping, "ping parses");
    Assert(WebChatProtocol.Parse("{\"type\":\"reset\"}").Kind == WebChatMessageKind.Reset, "reset parses");

    var message = WebChatProtocol.Parse("{\"type\":\"message\",\"text\":\"  你好  \"}");
    Assert(message.Kind == WebChatMessageKind.Message, "message parses");
    Assert(message.Text == "你好", "message text is trimmed");

    Assert(WebChatProtocol.Parse("{\"type\":\"message\",\"text\":\"   \"}").Kind == WebChatMessageKind.Invalid,
        "blank text is invalid, not an empty turn");
    Assert(WebChatProtocol.Parse("{\"type\":\"message\"}").Kind == WebChatMessageKind.Invalid, "missing text is invalid");
    Assert(WebChatProtocol.Parse("{\"type\":\"wat\"}").Kind == WebChatMessageKind.Unknown, "unknown type is its own kind");
    Assert(WebChatProtocol.Parse("not json").Kind == WebChatMessageKind.Invalid, "bad json is invalid");
    Assert(WebChatProtocol.Parse("{}").Kind == WebChatMessageKind.Invalid, "missing type is invalid");
    Assert(WebChatProtocol.Parse("[]").Kind == WebChatMessageKind.Invalid, "non-object is invalid");
    Assert(WebChatProtocol.Parse("").Kind == WebChatMessageKind.Invalid, "empty frame is invalid");
    Assert(WebChatProtocol.Parse("{\"type\":\"message\",\"text\":\"" +
                                 new string('x', WebChatProtocol.MaxPromptChars + 1) + "\"}").Kind
           == WebChatMessageKind.Invalid, "over-long prompt is refused");

    Assert(WebChatProtocol.Pong().Contains("\"pong\"", StringComparison.Ordinal), "pong encodes");
    Assert(WebChatProtocol.ResetAck().Contains("\"reset\"", StringComparison.Ordinal), "reset ack encodes");
    Assert(WebChatProtocol.Delta("你").Contains("\"delta\"", StringComparison.Ordinal), "delta encodes");
    Assert(WebChatProtocol.Done(3).Contains("\"chars\":3", StringComparison.Ordinal), "done carries the count");
    Assert(WebChatProtocol.Welcome().Contains("\"protocol\":1", StringComparison.Ordinal), "welcome names the protocol version");
}

// --- 4c. WebChat chat surface (socket, fake model) ----------------------
{
    var responder = new FakeResponder();
    var port = FreePort();
    using var server = new WebChatEndpoint(responder);
    server.Start(port);

    using var alice = await ChatClient.ConnectAsync(port);
    var welcome = await alice.ReceiveAsync();
    Assert(welcome.Contains("\"welcome\"", StringComparison.Ordinal), "connection starts with a welcome frame");

    await alice.SendAsync("{\"type\":\"ping\"}");
    Assert((await alice.ReceiveAsync()).Contains("\"pong\"", StringComparison.Ordinal), "ping answers pong");

    await alice.SendAsync("{\"type\":\"message\",\"text\":\"你好\"}");
    var deltas = new List<string>();
    while (true)
    {
        var frame = await alice.ReceiveAsync();
        if (frame.Contains("\"message\":" + "\"boom\"", StringComparison.Ordinal)) throw new Exception("unexpected error frame");
        if (frame.Contains("\"type\":\"delta\"", StringComparison.Ordinal))
        {
            deltas.Add(frame);
            continue;
        }

        Assert(frame.Contains("\"type\":\"done\"", StringComparison.Ordinal), "the turn ends with done");
        break;
    }

    Assert(deltas.Count == 3, "the reply arrives as several delta frames");
    Assert(deltas[0].Contains("答", StringComparison.Ordinal), "first delta carries the first chunk");
    Assert(responder.Calls.Count == 1, "the model was asked once");
    Assert(responder.Calls[0].HistoryCount == 0, "a first turn has no history");
    Assert(responder.Calls[0].Prompt == "你好", "the model receives the prompt");

    // Second turn on the same connection must carry the first exchange.
    await alice.SendAsync("{\"type\":\"message\",\"text\":\"再说一次\"}");
    await alice.DrainUntilDoneAsync();
    Assert(responder.Calls.Count == 2, "the model was asked again");
    Assert(responder.Calls[1].HistoryCount == 2, "the second turn sees the user and assistant messages");

    // A second client starts with its own, empty history.
    using var bob = await ChatClient.ConnectAsync(port);
    await bob.ReceiveAsync();
    await bob.SendAsync("{\"type\":\"message\",\"text\":\"我是鲍勃\"}");
    await bob.DrainUntilDoneAsync();
    Assert(responder.Calls[2].HistoryCount == 0, "connections do not share history");

    // Reset forgets this connection's history.
    await alice.SendAsync("{\"type\":\"reset\"}");
    Assert((await alice.ReceiveAsync()).Contains("\"reset\"", StringComparison.Ordinal), "reset is acknowledged");
    await alice.SendAsync("{\"type\":\"message\",\"text\":\"清空之后\"}");
    await alice.DrainUntilDoneAsync();
    Assert(responder.Calls[3].HistoryCount == 0, "reset clears the history");

    // Unknown and invalid frames are answered, never ignored.
    await alice.SendAsync("{\"type\":\"nope\"}");
    Assert((await alice.ReceiveAsync()).Contains("\"error\"", StringComparison.Ordinal), "unknown type is answered");
    await alice.SendAsync("{\"type\":\"message\",\"text\":\"\"}");
    Assert((await alice.ReceiveAsync()).Contains("\"error\"", StringComparison.Ordinal), "empty text is answered");

    // Two clients are counted, and dropping one is not a server failure.
    Assert(server.ClientCount == 2, "both clients are counted");
    bob.Dispose();
    await WaitAsync(() => server.ClientCount == 1);
    Assert(server.ClientCount == 1, "a dropped client is uncounted");

    // A model that throws must come back as an error frame, not a dead connection.
    await alice.SendAsync("{\"type\":\"message\",\"text\":\"boom\"}");
    var failure = await alice.ReceiveAsync();
    Assert(failure.Contains("\"error\"", StringComparison.Ordinal), "model failure is reported as an error frame");
    Assert(responder.Calls.Count == 5, "the failing turn is not retried behind the user's back");

    alice.Dispose();
    server.Stop();
    await WaitAsync(() => server.ClientCount == 0);
    Assert(!server.IsRunning, "server reports itself stopped");

    // Port is free again after stop.
    var rebound = new WebChatEndpoint(new FakeResponder());
    rebound.Start(port);
    Assert(rebound.Port == port, "stop releases the port");
    rebound.Dispose();
}

// --- 4e. 客户端在流式回复中途断开 -------------------------------------
{
    var slow = new SlowResponder();
    var port = FreePort();
    using var server = new WebChatEndpoint(slow);
    server.Start(port);

    var alice = await ChatClient.ConnectAsync(port);
    await alice.ReceiveAsync();                       // welcome
    await alice.SendAsync("{\"type\":\"message\",\"text\":\"慢慢说\"}");
    await alice.ReceiveAsync();                       // first delta
    await alice.ReceiveAsync();                       // second delta
    alice.Dispose();                                  // walk away mid-answer

    await WaitAsync(() => server.ClientCount == 0, 3000);
    Assert(server.ClientCount == 0, "a client that leaves mid-answer is uncounted");

    var producedAtAbort = slow.Produced;

    // Wait longer than the whole generation would take (400 chunks x 10 ms = 4 s). If the aborted
    // turn kept running, Produced would reach Total; the point of the test is that it does not.
    await Task.Delay(5000);
    var producedLater = slow.Produced;
    Assert(producedLater < slow.Total,
        $"generation does not run to completion after the client left (produced {producedLater} of {slow.Total})");
    Assert(producedLater - producedAtAbort < 60,
        $"it stops soon after the abort (produced {producedAtAbort} before, {producedLater} after)");
    Console.WriteLine($"   abort test: {producedAtAbort} chunks before the abort, {producedLater} after 5s of {slow.Total}");

    // The server must still work for everyone else.
    using var bob = await ChatClient.ConnectAsync(port);
    await bob.ReceiveAsync();
    await bob.SendAsync("{\"type\":\"ping\"}");
    Assert((await bob.ReceiveAsync()).Contains("\"pong\"", StringComparison.Ordinal),
        "the server still answers after an aborted turn");

    bob.Dispose();
    server.Stop();
    await WaitAsync(() => server.ClientCount == 0, 2000);
    Assert(slow.Produced < slow.Total, $"generation stopped early (produced {slow.Produced} of {slow.Total})");
}

// --- 4f. 长帧与超长文本 ------------------------------------------------
{
    var responder = new FakeResponder();
    var port = FreePort();
    using var server = new WebChatEndpoint(responder);
    server.Start(port);

    using var client = await ChatClient.ConnectAsync(port);
    await client.ReceiveAsync();                       // welcome

    // Longer than 125 bytes, so the frame uses the 16-bit extended length. Chinese is 3 bytes per
    // character, which makes it easy to cross the boundary: 60 characters is 180 bytes.
    var longPrompt = new string('长', 60);
    Assert(Encoding.UTF8.GetByteCount(longPrompt) > 125, "the probe prompt really needs an extended frame");
    await client.SendAsync($"{{\"type\":\"message\",\"text\":\"{longPrompt}\"}}");
    var reply = await client.DrainUntilDoneAsync();
    Assert(reply.Contains("\"type\":\"done\"", StringComparison.Ordinal), "an extended-length frame is read");
    Assert(responder.Calls[^1].Prompt == longPrompt, "the whole prompt arrived intact, not truncated");

    // Over the documented per-message limit: refused with a readable reason, connection stays open.
    var tooLong = new string('x', WebChatProtocol.MaxPromptChars + 1);
    await client.SendAsync($"{{\"type\":\"message\",\"text\":\"{tooLong}\"}}");
    var refusal = await client.ReceiveAsync();
    Assert(refusal.Contains("\"type\":\"error\"", StringComparison.Ordinal), "an over-long prompt is refused");
    Assert(refusal.Contains(WebChatProtocol.MaxPromptChars.ToString(), StringComparison.Ordinal),
        "the refusal names the limit");

    await client.SendAsync("{\"type\":\"ping\"}");
    Assert((await client.ReceiveAsync()).Contains("\"pong\"", StringComparison.Ordinal),
        "the connection survives a refused message");

    // Larger than the frame limit: the server drops the connection rather than truncating.
    var huge = new string('y', WebSocketFraming.MaxPayloadBytes + 1024);
    var closed = false;
    try
    {
        await client.SendUncheckedAsync($"{{\"type\":\"message\",\"text\":\"{huge}\"}}");
        await client.ReceiveAsync();
    }
    catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
    {
        closed = true;
    }

    await WaitAsync(() => server.ClientCount == 0, 3000);
    Assert(closed && server.ClientCount == 0, "an oversized frame ends the connection instead of being truncated");

    server.Stop();
}

// --- 4g. 历史裁剪（每连接，默认 20 条）--------------------------------
{
    var responder = new FakeResponder();
    var port = FreePort();
    using var server = new WebChatEndpoint(responder);
    server.Start(port);

    using var client = await ChatClient.ConnectAsync(port);
    await client.ReceiveAsync();                        // welcome

    for (var turn = 1; turn <= 12; turn++)
    {
        await client.SendAsync($"{{\"type\":\"message\",\"text\":\"第{turn}轮\"}}");
        await client.DrainUntilDoneAsync();
    }

    Assert(responder.Histories.Count == 12, "every turn reached the model");
    Assert(responder.Histories[0].Count == 0, "the first turn has no history");
    Assert(responder.Histories[9].Count == 18, "history grows by two per turn");
    Assert(responder.Histories.Max(h => h.Count) == 20, "history is capped at the documented 20 messages");
    Assert(responder.Histories[11].Count == 20, "the cap holds instead of growing without bound");

    // The cap must drop the oldest exchange, not the newest: the model has to see what was just said.
    var last = responder.Histories[11];
    Assert(last[^2] == "user:第11轮", "the newest user message is kept");
    Assert(last[^1].StartsWith("assistant:", StringComparison.Ordinal), "the newest reply is kept");
    Assert(last[0] != "user:第1轮", "the oldest exchange is the one dropped");

    // The limit is a parameter, and it is honoured rather than ignored.
    var tight = new FakeResponder();
    var tightPort = FreePort();
    using var tightServer = new WebChatEndpoint(tight, maxHistoryMessages: 4);
    tightServer.Start(tightPort);
    using var tightClient = await ChatClient.ConnectAsync(tightPort);
    await tightClient.ReceiveAsync();
    for (var turn = 1; turn <= 5; turn++)
    {
        await tightClient.SendAsync($"{{\"type\":\"message\",\"text\":\"t{turn}\"}}");
        await tightClient.DrainUntilDoneAsync();
    }

    Assert(tight.Histories.Max(h => h.Count) == 4, "a smaller configured limit is honoured");

    client.Dispose();
    tightClient.Dispose();
    server.Stop();
    tightServer.Stop();
}

// --- 5. Localisation ----------------------------------------------------
{
    var zh = new Localizer("zh-CN");
    var en = new Localizer("en-US");
    Assert(zh["messaging.status.ready"] == "可用", "zh string");
    Assert(en["messaging.status.ready"] == "Ready", "en string");
    Assert(zh["messaging.status.ready"] != en["messaging.status.ready"], "languages differ");

    var unknownLanguage = new Localizer("fr-FR");
    Assert(unknownLanguage.Language == Localizer.DefaultLanguage, "unsupported language falls back");
    Assert(unknownLanguage["messaging.title"] == "消息平台", "fallback returns default language");

    Assert(en["no.such.key"] == "no.such.key", "missing key returns the key");
    Assert(Localizer.SupportedLanguages.Count >= 2, "at least two languages");
}

Console.WriteLine($"All {checks} messaging checks passed.");

static int FreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

static string MissingPath() => Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N") + ".cmd");

static async Task WaitAsync(Func<bool> condition, int timeoutMs = 3000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (!condition())
    {
        if (Environment.TickCount64 > deadline) return;
        await Task.Delay(20);
    }
}

static string TempFile()
{
    var path = Path.Combine(Path.GetTempPath(), "openclaw-" + Guid.NewGuid().ToString("N") + ".cmd");
    File.WriteAllText(path, "@echo off");
    return path;
}

static HttpClient HttpClientWith(HttpStatusCode code, string body) =>
    new(new StubHandler(code, body)) { Timeout = TimeSpan.FromSeconds(5) };

sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

sealed class FakeCli : ICliRunner
{
    public List<string> Calls { get; } = [];
    public Func<string, CliResult> Respond { get; set; } = _ => new(true, 0, "");

    public Task<CliResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(arguments);
        return Task.FromResult(Respond(arguments));
    }
}

/// <summary>
/// Stands in for the model. Records what it was asked so the tests can prove the server passes the
/// right history and prompt, and fails on demand to prove a broken turn is reported, not swallowed.
/// </summary>
sealed class FakeResponder : IChatResponder
{
    public List<(int HistoryCount, string Prompt)> Calls { get; } = [];

    /// <summary>Role-tagged snapshot of each turn's history, so trimming can be checked.</summary>
    public List<List<string>> Histories { get; } = [];

    public async IAsyncEnumerable<string> RespondAsync(
        IReadOnlyList<ChatMessage> history,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add((history.Count, prompt));
        Histories.Add(history.Select(message => $"{message.Role}:{message.Content}").ToList());

        if (prompt == "boom") throw new InvalidOperationException("model exploded");

        foreach (var chunk in new[] { "答", "案:", prompt })
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}

/// <summary>Minimal WebSocket client, so the tests talk to the server the way a browser would.</summary>
sealed class ChatClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private ChatClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static async Task<ChatClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        var stream = client.GetStream();
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        await WebSocketFraming.WriteTextAsync(stream,
            "GET / HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n");

        var header = await WebSocketFraming.ReadHeaderAsync(stream)
                     ?? throw new Exception("handshake had no answer");
        if (!header.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new Exception("handshake refused: " + header.Split("\r\n")[0]);
        if (!header.Contains(WebSocketFraming.Accept(key), StringComparison.Ordinal))
            throw new Exception("Sec-WebSocket-Accept mismatch");

        return new ChatClient(client, stream);
    }

    public Task SendAsync(string frame) => WebSocketFraming.WriteMaskedFrameAsync(_stream, frame);

    /// <summary>
    /// Writes a masked frame without the client-side size guard, so the server's own refusal can be
    /// tested. Above 65535 bytes the length goes into the 64-bit form, which the server rejects.
    /// </summary>
    public async Task SendUncheckedAsync(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var mask = new byte[] { 1, 2, 3, 4 };
        byte[] frame;

        if (bytes.Length > 65535)
        {
            frame = new byte[bytes.Length + 14];
            frame[0] = 0x81;
            frame[1] = 0x80 | 127;
            var length = (ulong)bytes.Length;
            for (var i = 0; i < 8; i++) frame[2 + i] = (byte)(length >> (56 - i * 8));
            mask.CopyTo(frame, 10);
            for (var i = 0; i < bytes.Length; i++) frame[14 + i] = (byte)(bytes[i] ^ mask[i % 4]);
        }
        else
        {
            frame = new byte[bytes.Length + 8];
            frame[0] = 0x81;
            frame[1] = 0x80 | 126;
            frame[2] = (byte)(bytes.Length >> 8);
            frame[3] = (byte)(bytes.Length & 0xFF);
            mask.CopyTo(frame, 4);
            for (var i = 0; i < bytes.Length; i++) frame[8 + i] = (byte)(bytes[i] ^ mask[i % 4]);
        }

        await _stream.WriteAsync(frame);
        await _stream.FlushAsync();
    }

    public async Task<string> ReceiveAsync()
    {
        return await WebSocketFraming.ReadTextFrameAsync(_stream)
               ?? throw new Exception("connection closed before a frame arrived");
    }

    /// <summary>Reads deltas until the turn ends with done or error.</summary>
    public async Task<string> DrainUntilDoneAsync()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var frame = await ReceiveAsync();
            if (frame.Contains("\"type\":\"delta\"", StringComparison.Ordinal)) text.Append(frame);
            else return text.Append(frame).ToString();
        }
    }

    public void Dispose()
    {
        try { _client.Dispose(); } catch { }
    }
}


/// <summary>A web chat control that records calls instead of opening a socket.</summary>
sealed class RecordingWebChatControl : MyAiAssistant.Services.Dashboard.IWebChatControl
{
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool RefuseStart { get; set; }

    public MyAiAssistant.Services.Dashboard.WebChatStatus Status() =>
        new(false, 9881, "ws://127.0.0.1:9881/", 0);

    public (bool Ok, string Message) Start()
    {
        StartCalls++;
        return RefuseStart
            ? (false, "无法绑定端口 9881（AddressAlreadyInUse），端口可能已被占用。")
            : (true, "WebChat 服务已启动：ws://127.0.0.1:9881/");
    }

    public (bool Ok, string Message) Stop()
    {
        StopCalls++;
        return (true, "WebChat 服务已停止。");
    }
}

/// <summary>Answers in many small chunks, slowly, and records how far it got.</summary>
sealed class SlowResponder : IChatResponder
{
    public int Total { get; init; } = 400;
    public int Produced;

    public async IAsyncEnumerable<string> RespondAsync(
        IReadOnlyList<ChatMessage> history,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < Total; i++)
        {
            await Task.Delay(10, cancellationToken);
            Produced++;
            yield return "片";
        }
    }
}