using System.IO;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.LocalSend;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var tempRoot = Path.Combine(Path.GetTempPath(), "fairyai-lsr-" + Guid.NewGuid().ToString("N"));
var saveDir = Path.Combine(tempRoot, "received");
Directory.CreateDirectory(saveDir);

try
{
    // ---- 1. Receiving asks the user through the shared pending queue ----
    {
        var center = new AgentControlCenter();
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit1"));
        using var coordinator = new LocalSendReceiveCoordinator(
            center, audit,
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "fp", 0, saveDir));

        var incoming = new LocalSendIncoming("Pixel", "peer-fp",
            new[] { new LocalSendFile("1", "photo.png", 2048, "image/png") });

        var asking = coordinator.ConfirmAsync(incoming, CancellationToken.None);
        await Task.Delay(300);

        var pending = center.Pending();
        Assert(pending.Count == 1, "the incoming transfer becomes one pending question");
        Assert(pending[0].Risk == RiskCategory.ExternalTransfer, "it is filed as an external transfer");
        Assert(pending[0].Prompt.Contains("Pixel", StringComparison.Ordinal), "the sender is named");
        Assert(pending[0].Prompt.Contains("photo.png", StringComparison.Ordinal), "the file names are listed");
        Assert(pending[0].Prompt.Contains("2 KB", StringComparison.Ordinal), "the size is shown in readable units");

        center.Answer(pending[0].Id, approved: true);
        Assert(await asking, "approving the question accepts the transfer");

        Assert(center.Pending().Count == 0, "no pending item is left behind");
        var records = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var record = records.LastOrDefault(r => r.ActionId == "localsend.receive");
        Assert(record is not null, "the decision is audited");
        Assert(record!.Decision == "allowed", "the audit records that it was allowed");
        Assert(record.Category == RiskCategory.ExternalTransfer, "the audit carries the transfer category");
        Assert(record.ParameterNames is { Count: 1 } names && names[0] == "photo.png",
            "only the file name is kept as a parameter, not its contents");
    }

    // ---- 2. Declining is a denial, and it is audited as one ----
    {
        var center = new AgentControlCenter();
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit2"));
        using var coordinator = new LocalSendReceiveCoordinator(
            center, audit,
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "fp", 0, saveDir));

        var incoming = new LocalSendIncoming("Unknown", "x",
            new[] { new LocalSendFile("1", "payload.exe", 10, "application/octet-stream") });

        var asking = coordinator.ConfirmAsync(incoming, CancellationToken.None);
        await Task.Delay(300);
        center.Answer(center.Pending()[0].Id, approved: false);
        Assert(!await asking, "declining refuses the transfer");

        var record = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1))
            .Last(r => r.ActionId == "localsend.receive");
        Assert(record.Decision == "denied", "the refusal is audited as denied");
    }

    // ---- 3. A cancelled wait (timeout path) refuses instead of hanging or accepting ----
    {
        var center = new AgentControlCenter();
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit3"));
        using var coordinator = new LocalSendReceiveCoordinator(
            center, audit,
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "fp", 0, saveDir));

        var incoming = new LocalSendIncoming("Slow", "y",
            new[] { new LocalSendFile("1", "a.bin", 1, "application/octet-stream") });

        using var cancel = new CancellationTokenSource();
        var asking = coordinator.ConfirmAsync(incoming, cancel.Token);
        await Task.Delay(200);
        cancel.Cancel();
        Assert(!await asking, "cancelling the question is a refusal, not consent");
    }

    // ---- 4. Starting and stopping the listener is explicit ----
    {
        var center = new AgentControlCenter();
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit4"));
        var coordinator = new LocalSendReceiveCoordinator(
            center, audit,
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "fp", 0, saveDir));

        Assert(!coordinator.IsRunning, "the receiver does not listen before it is started");
        var started = coordinator.Start();
        if (started)
        {
            Assert(coordinator.IsRunning, "starting opens the listener");
            Assert(coordinator.Port > 0, "the bound port is reported");
            coordinator.Stop();
            Assert(!coordinator.IsRunning, "stopping closes the listener");
        }
        else
        {
            Console.WriteLine($"  note: listener could not bind here ({coordinator.LastError})");
            Assert(coordinator.LastError is not null, "a failed start reports why instead of pretending");
        }
        coordinator.Dispose();
    }

    Console.WriteLine($"All {checks} LocalSend receive checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED after {checks} checks: {ex.Message}");
    return 1;
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch (IOException) { }
}
