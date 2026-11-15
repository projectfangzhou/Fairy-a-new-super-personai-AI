using System.Text.Json;
using MyAiAssistant.Services;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-summary-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ---------------------------------------------------------------- tokens

    var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens"));
    Assert(tokens.Devices.Count == 0, "no device is trusted before pairing");

    var token = tokens.Issue("Pixel 8");
    Assert(token.Length >= 40, "the issued token is a long random value");
    Assert(tokens.Verify("Pixel 8", token), "the issued token verifies");
    Assert(!tokens.Verify("Pixel 8", token + "x"), "a modified token fails");
    Assert(!tokens.Verify("iPhone", token), "the same token does not work for another device name");
    Assert(!tokens.Verify("Pixel 8", null), "a missing token fails");
    Assert(!tokens.Verify(null, token), "a missing device name fails");
    Assert(tokens.Devices.Contains("Pixel 8"), "the device is listed");

    var rawFile = File.ReadAllText(tokens.StorePath);
    Assert(!rawFile.Contains(token, StringComparison.Ordinal), "only the hash is stored, never the token");

    var reissued = tokens.Issue("Pixel 8");
    Assert(!tokens.Verify("Pixel 8", token), "re-pairing invalidates the previous token");
    Assert(tokens.Verify("Pixel 8", reissued), "and the new one works");

    Assert(tokens.Revoke("Pixel 8"), "revoking succeeds");
    Assert(!tokens.Verify("Pixel 8", reissued), "a revoked token stops working");
    Assert(!tokens.Revoke("Pixel 8"), "revoking twice reports nothing to do");

    var reloaded = new PairedDeviceTokens(Path.Combine(directory, "tokens"));
    var persisted = reloaded.Issue("Tab");
    Assert(new PairedDeviceTokens(Path.Combine(directory, "tokens")).Verify("Tab", persisted),
        "tokens survive a restart");

    // ---------------------------------------------------------------- the gate

    Assert(SummarySyncAccess.RejectReason(enabled: false, tokenVerified: true) == SummarySyncAccess.DisabledCode,
        "sync off means no summary, even for a paired device");
    Assert(SummarySyncAccess.RejectReason(enabled: true, tokenVerified: false) == SummarySyncAccess.UnpairedCode,
        "sync on still requires a valid token");
    Assert(SummarySyncAccess.RejectReason(enabled: false, tokenVerified: false) == SummarySyncAccess.DisabledCode,
        "the opt-in is checked first so the answer does not leak token validity");
    Assert(SummarySyncAccess.RejectReason(enabled: true, tokenVerified: true) is null,
        "both conditions met means the request proceeds");

    // ---------------------------------------------------------------- payload shape

    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    var summaries = new[]
    {
        new TaskSummary("t1", "整理 D:\\Dev\\客户报价\\2026.xlsx", "Running", now.AddMinutes(-3)),
        new TaskSummary("t2", "修复登录超时", "Completed", now.AddMinutes(-5)),
        new TaskSummary("t2", "修复登录超时（重试）", "Failed", now.AddMinutes(-1)),
        new TaskSummary("t3", "读取 /home/user/.ssh/id_rsa", "Paused", now.AddMinutes(-9)),
    };

    var json = SummarySyncPayload.Build(summaries);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    Assert(root.TryGetProperty("generatedAt", out _), "the payload carries a timestamp");

    var tasks = root.GetProperty("tasks");
    Assert(tasks.GetArrayLength() == 3, "duplicate task ids collapse to one entry");

    var first = tasks[0];
    Assert(first.GetProperty("taskId").GetString() == "t2", "newest first");
    Assert(first.GetProperty("state").GetString() == "Failed", "the newest state wins for a repeated id");

    foreach (var task in tasks.EnumerateArray())
    {
        Assert(task.TryGetProperty("title", out _), "each entry has a title");
        var title = task.GetProperty("title").GetString() ?? "";
        Assert(!title.Contains("客户报价", StringComparison.Ordinal), "a path in a title never leaves the machine");
        Assert(!title.Contains("id_rsa", StringComparison.Ordinal), "a posix path never leaves either");
        Assert(title.Length <= 41, "titles are truncated");

        // The shape is the contract: nothing else may appear.
        var properties = task.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert(properties.SetEquals(new[] { "taskId", "title", "state", "updatedAt" }),
            "an entry carries exactly four fields");
    }

    Assert(!json.Contains("error", StringComparison.OrdinalIgnoreCase), "no error text is included");
    Assert(!json.Contains("prompt", StringComparison.OrdinalIgnoreCase), "no prompt is included");
    Assert(!json.Contains("output", StringComparison.OrdinalIgnoreCase), "no output is included");
    Assert(!json.Contains("D:\\\\Dev", StringComparison.Ordinal), "no raw path is included");

    var many = Enumerable.Range(0, 60)
        .Select(index => new TaskSummary("t" + index, "任务" + index, "Running", now.AddSeconds(index)))
        .ToArray();
    using var capped = JsonDocument.Parse(SummarySyncPayload.Build(many));
    Assert(capped.RootElement.GetProperty("tasks").GetArrayLength() == SummarySyncPayload.DefaultMaxTasks,
        "the payload is capped");

    using var explicitCap = JsonDocument.Parse(SummarySyncPayload.Build(many, 5));
    Assert(explicitCap.RootElement.GetProperty("tasks").GetArrayLength() == 5, "an explicit cap is honoured");

    using var empty = JsonDocument.Parse(SummarySyncPayload.Build(Array.Empty<TaskSummary>()));
    Assert(empty.RootElement.GetProperty("tasks").GetArrayLength() == 0, "an empty list is a valid payload");

    var threw = false;
    try { SummarySyncPayload.Build(null!); }
    catch (ArgumentNullException) { threw = true; }
    Assert(threw, "a null source is a programming error, not a silent empty list");

    // ---------------------------------------------------------------- the switch and the revoke list

    {
        var controlTokens = new PairedDeviceTokens(Path.Combine(directory, "control-tokens"));
        var enabled = false;
        var applied = new List<bool>();
        var control = new SummarySyncControl(controlTokens, () => enabled, value => { enabled = value; applied.Add(value); });

        var initial = control.Read();
        Assert(!initial.Enabled && initial.PairedDeviceCount == 0, "nothing is on to begin with");

        var noDevices = control.Apply(true);
        Assert(noDevices.Ok && enabled, "turning it on persists the switch");
        Assert(noDevices.Message.Contains("配对", StringComparison.Ordinal),
            "with no paired device the message points at pairing instead of pretending it works");
        Assert(applied.SequenceEqual(new[] { true }), "the switch was written once");

        var phoneToken = controlTokens.Issue("Pixel 8");
        var withDevice = control.Read();
        Assert(withDevice.Enabled && withDevice.PairedDeviceCount == 1, "a paired device shows up");
        Assert(withDevice.Devices[0].DeviceName == "Pixel 8", "the device is named");

        var off = control.Apply(false);
        Assert(off.Ok && !enabled && off.Message.Contains("403", StringComparison.Ordinal),
            "turning it off says what the paired devices will now get");

        var revoked = control.Revoke("Pixel 8");
        Assert(revoked.Ok, "revoking a known device succeeds");
        Assert(control.Read().PairedDeviceCount == 0, "and the list is empty afterwards");
        Assert(!controlTokens.Verify("Pixel 8", phoneToken), "the revoked token no longer verifies");

        Assert(!control.Revoke("Pixel 8").Ok, "revoking twice reports nothing to do");
        Assert(!control.Revoke("  ").Ok, "an empty device name is refused");

        // The paired-control switch is a separate permission, never toggled with the summary one.
        var pairControl = false;
        var pairControlWrites = new List<bool>();
        var withPairControl = new SummarySyncControl(controlTokens,
            () => enabled, value => enabled = value,
            () => pairControl, value => { pairControl = value; pairControlWrites.Add(value); });

        var summaryBefore = withPairControl.Read().Enabled;
        var pairApplied = withPairControl.ApplyPairControl(true);
        Assert(pairApplied.Ok && pairControl, "paired control has its own switch");
        Assert(withPairControl.Read().Enabled == summaryBefore,
            "turning paired control on does not touch the summary switch");
        Assert(withPairControl.Read().PairControlEnabled, "and the view reports it");
        Assert(pairControlWrites.SequenceEqual(new[] { true }), "only the paired-control setter was called");
    }

    Console.WriteLine($"All {checks} summary sync checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}
