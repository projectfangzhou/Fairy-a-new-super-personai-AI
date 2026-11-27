using System.Net;
using System.Net.Sockets;
using System.Text;
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

    // Probe twice on the same port: the second run only succeeds if the first released its listener.
    var freePort = FreePort();
    var probe = await new WebChatAdapter(freePort).ProbeAsync();
    Assert(probe.State == PlatformProbeState.Ready, "probe completes a real websocket round trip");
    Assert(probe.Message.Contains("ws://127.0.0.1:", StringComparison.Ordinal), "readiness names the local url");

    var again = await new WebChatAdapter(freePort).ProbeAsync();
    Assert(again.State == PlatformProbeState.Ready, "probe releases its port");

    // A taken port must fail: reporting ready there is exactly the lie this catalogue avoids.
    var squatter = new WebChatEndpoint();
    squatter.Start(0);
    try
    {
        var blocked = await new WebChatAdapter(squatter.Port).ProbeAsync();
        Assert(blocked.State == PlatformProbeState.Failed, "occupied port fails");
        Assert(!blocked.IsReady, "occupied port never reports ready");
        Assert(!blocked.Message.Contains("ws://", StringComparison.Ordinal), "failure names no url");
    }
    finally
    {
        squatter.Dispose();
    }

    // The account path is what the UI actually calls, so prove it end to end rather than
    // only the adapter in isolation.
    var config = new AppConfig();
    var store = new MessagingAccountStore(() => config, saved => config = saved);
    var added = store.Add(PlatformKind.WebChat, "本机网页", null);
    Assert(added.Ok, "web chat account adds with no credentials at all");
    Assert(added.Id is not null, "added account comes back with an id");

    var viaService = await new MessagingAccountService(store).ProbeAsync(added.Id!);
    Assert(viaService.State == PlatformProbeState.Ready, "account probe path reports ready");
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
