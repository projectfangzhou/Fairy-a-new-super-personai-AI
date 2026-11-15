using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;

// A bench for the desktop's paired-phone endpoints.
//
// The decisions are the shipping ones: ConfigManager.VerifyPairingCode for the code, PairedDeviceTokens
// for the bearer token, SummarySyncAccess for who may read, PairedTaskBridge for what the phone sees and
// for routing an answer back into the same pending item the desktop shows.
//
// The HTTP plumbing is Kestrel rather than LanSyncService's HttpListener, and the JSON below is copied
// from the handlers in LanSyncService so the shapes match byte for byte. It is a stand-in for this
// machine only: .NET 8's HttpListener refuses to start here ("handle is invalid"), while the framework
// version PowerShell uses starts fine.

const int Port = 9899;
const string PairingCode = "123456";

var baseDirectory = AppContext.BaseDirectory;
var logPath = Path.Combine(baseDirectory, "deskbench.log");

if (args.Length > 0 && args[0] == "--listen-check")
{
    Console.WriteLine($"HttpListener on this runtime: {(TryHttpListener(Port) ? "usable" : "refuses to start")}");
    return 0;
}

static bool TryHttpListener(int port)
{
    try
    {
        var probe = new System.Net.HttpListener();
        probe.Prefixes.Add($"http://127.0.0.1:{port}/");
        probe.Start();
        probe.Stop();
        probe.Close();
        return true;
    }
    catch
    {
        return false;
    }
}

void Log(string message)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
    Console.WriteLine(line);
    try
    {
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch
    {
        // Logging must never take the bench down.
    }
}

var config = ConfigManager.Load();
config.Sync.FileServerPort = Port;
config.Sync.DeviceName = "DESKBENCH";
config.Sync.PairingCode = ConfigManager.HashPairingCode(PairingCode);
config.Sync.SummarySyncEnabled = true;
config.Sync.PairControlEnabled = true;
ConfigManager.Save(config);
Log($"config: port={Port} pairingCode={PairingCode} summarySync=on pairControl=on");

var center = new AgentControlCenter();
var tokens = new PairedDeviceTokens(Path.Combine(baseDirectory, "sync"));
var bridge = new PairedTaskBridge(center, tokens, () => ConfigManager.Load().Sync.PairControlEnabled);

var running = center.Enqueue("整理客户报价表的对比表");
running.Start();
running.ReportProgress(1, 3, "读取表格");

var waiting = center.Enqueue("把整理好的报价发到邮箱");
waiting.Start();
var approval = waiting.AskApprovalAsync("要把这份报价发给 zhang@example.com，可以吗？", RiskCategory.ExternalTransfer);
Log($"seeded: running={running.Id} waiting={waiting.Id} pending={center.Pending().Count}");

// All interfaces: the emulator's 10.0.2.2 alias is documented as "the host's loopback", but with the
// listener bound to 127.0.0.1 the phone's connection was refused, so this binds wide and the bench is
// only ever run for a few minutes at a time.
Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{Port}");
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();

string? Header(HttpContext context, string name) => context.Request.Headers[name].FirstOrDefault();

// POST /pair — the code is the only credential; the token it returns is what every later call needs.
app.MapPost("/pair", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();

    string code;
    try
    {
        using var document = JsonDocument.Parse(body);
        code = document.RootElement.TryGetProperty("code", out var element) ? element.GetString() ?? "" : "";
    }
    catch (JsonException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { success = false, error = "bad_json" });
        return;
    }

    var device = Header(context, "X-From-Device") ?? "unknown";
    var storedHash = ConfigManager.Load().Sync.PairingCode;
    if (string.IsNullOrEmpty(storedHash))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { success = false, error = "no_code" });
        return;
    }

    var success = ConfigManager.VerifyPairingCode(code, storedHash);
    var issued = success ? tokens.Issue(device) : null;
    Log($"pair from {device}: {(success ? "accepted" : "refused")}");
    await context.Response.WriteAsJsonAsync(new { success, deviceToken = issued });
});

// GET /summary — read-only task summaries, opt-in on this side and token-authenticated.
app.MapGet("/summary", async (HttpContext context) =>
{
    var device = Header(context, "X-From-Device");
    var rejection = SummarySyncAccess.RejectReason(
        ConfigManager.Load().Sync.SummarySyncEnabled, tokens.Verify(device, Header(context, "X-Device-Token")));

    if (rejection is not null)
    {
        Log($"summary rejected ({rejection}) for {device ?? "unknown"}");
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { success = false, error = rejection });
        return;
    }

    var summaries = center.Tasks()
        .Select(snapshot => new TaskSummary(snapshot.Id, snapshot.Title, snapshot.State.ToString(), snapshot.UpdatedAt))
        .ToList();

    Log($"summary served to {device}: {summaries.Count} task(s)");
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(SummarySyncPayload.Build(summaries));
});

// GET /desk — the task list plus everything waiting for an answer.
app.MapGet("/desk", async (HttpContext context) =>
{
    var device = Header(context, "X-From-Device");
    var refusal = bridge.RejectReason(device, Header(context, "X-Device-Token"));
    if (refusal is not null)
    {
        Log($"desk rejected ({refusal}) for {device ?? "unknown"}");
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { success = false, error = refusal });
        return;
    }

    var desk = bridge.Read();
    Log($"desk served to {device}: {desk.Tasks.Count} task(s), {desk.Pending.Count} pending");
    await context.Response.WriteAsJsonAsync(new
    {
        success = true,
        generatedAt = desk.GeneratedAt,
        tasks = desk.Tasks.Select(task => new
        {
            taskId = task.TaskId,
            title = task.Title,
            state = task.State,
            canPause = task.CanPause,
            canStop = task.CanStop,
            updatedAt = task.UpdatedAt
        }),
        pending = desk.Pending.Select(item => new
        {
            interactionId = item.InteractionId,
            taskId = item.TaskId,
            kind = item.Kind,
            prompt = item.Prompt,
            risk = item.Risk,
            createdAt = item.CreatedAt
        })
    });
});

// POST /desk/answer — goes into the very item the desktop is waiting on.
app.MapPost("/desk/answer", async (HttpContext context) =>
{
    var device = Header(context, "X-From-Device");
    var refusal = bridge.RejectReason(device, Header(context, "X-Device-Token"));
    if (refusal is not null)
    {
        Log($"answer rejected ({refusal}) for {device ?? "unknown"}");
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { success = false, error = refusal });
        return;
    }

    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();

    PairedDecision decision;
    try
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var interactionId = root.TryGetProperty("interactionId", out var idElement) ? idElement.GetString() : null;

        if (root.TryGetProperty("approved", out var approvedElement) &&
            approvedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            decision = bridge.Answer(interactionId, approvedElement.GetBoolean());
        else if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            decision = bridge.AnswerText(interactionId, textElement.GetString());
        else
            decision = new PairedDecision(false, "request body needs approved or text");
    }
    catch (JsonException)
    {
        decision = new PairedDecision(false, "request body is not JSON");
    }

    Log($"answer from {device}: ok={decision.Ok} ({decision.Message})");
    context.Response.StatusCode = decision.Ok ? 200 : 409;
    await context.Response.WriteAsJsonAsync(new { success = decision.Ok, message = decision.Message });
});

app.MapGet("/ping", () => Results.Text("pong"));

await app.StartAsync();
Log($"READY http://127.0.0.1:{Port} (emulator: http://10.0.2.2:{Port})");

string? lastPendingCount = null;
var approvalSeen = false;

while (true)
{
    var pendingNow = center.Pending().Count.ToString();
    if (pendingNow != lastPendingCount)
    {
        Log($"pending interactions: {pendingNow}");
        lastPendingCount = pendingNow;
    }

    if (approval.IsCompleted && !approvalSeen)
    {
        approvalSeen = true;
        Log($"approval answered from the phone: {(approval.Result ? "allowed" : "denied")}");
    }
    else if (!approval.IsCompleted)
    {
        approvalSeen = false;
    }

    await Task.Delay(1000);
}
