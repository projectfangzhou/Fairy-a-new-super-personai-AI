using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-permissions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var policy = new PermissionPolicy();

    // 1. Low-risk actions pass without any approver.
    var allowRecord = new ActionRequest("chat.read", RiskCategory.None, "读取聊天摘要");
    Assert(policy.Evaluate(allowRecord).Verdict == PermissionVerdict.Allow, "low risk allowed by rule");

    // 2. Every risky category reaches the user, never auto-approved.
    foreach (var category in Enum.GetValues<RiskCategory>().Where(c => c != RiskCategory.None))
    {
        var decision = policy.Evaluate(new ActionRequest("test.action", category, "测试"));
        Assert(decision.Verdict == PermissionVerdict.RequireApproval, $"{category} requires approval");
    }

    // 3. Boot/firmware writes are refused outright, not merely offered for approval.
    foreach (var target in new[] { @"C:\EFI\BOOT\bootx64.efi", @"C:\Windows\System32\config\SYSTEM", @"\\.\PhysicalDrive0" })
    {
        var decision = policy.Evaluate(new ActionRequest("file.write", RiskCategory.DeleteOrOverwriteData, "写入", target));
        Assert(decision.Verdict == PermissionVerdict.Deny, $"hard deny {target}");
    }

    // 4. No approver registered => fail closed.
    {
        var audit = new OperationAudit(directory);
        var gate = new ApprovalGate(policy, audit);
        var result = await gate.AuthorizeAsync(new ActionRequest("file.delete", RiskCategory.DeleteOrOverwriteData, "删除报告", @"C:\work\a.txt"));
        Assert(!result.IsAllowed && result.Decision == "no-approver", "missing approver fails closed");
    }

    // 5. Approver answers yes / no.
    {
        var audit = new OperationAudit(directory);
        var gate = new ApprovalGate(policy, audit) { Approver = (_, _) => Task.FromResult(true) };
        var yes = await gate.AuthorizeAsync(new ActionRequest("file.delete", RiskCategory.DeleteOrOverwriteData, "删除报告"));
        Assert(yes.IsAllowed && yes.Decision == "allowed", "user approval allows");

        gate.Approver = (_, _) => Task.FromResult(false);
        var no = await gate.AuthorizeAsync(new ActionRequest("file.delete", RiskCategory.DeleteOrOverwriteData, "删除报告"));
        Assert(!no.IsAllowed && no.Decision == "user-denied", "user refusal denies");
    }

    // 6. Denied-by-policy never bothers the user.
    {
        var audit = new OperationAudit(directory);
        var calls = 0;
        var gate = new ApprovalGate(policy, audit) { Approver = (_, _) => { calls++; return Task.FromResult(true); } };
        var blocked = await gate.AuthorizeAsync(new ActionRequest("boot.write", RiskCategory.ModifySystemSecurityOrBoot, "改启动项", @"C:\EFI\x"));
        Assert(!blocked.IsAllowed && blocked.Decision == "denied-by-policy" && calls == 0, "policy denial bypasses the user prompt");
    }

    // 7. A broken approver and a timeout both deny instead of permitting.
    {
        var audit = new OperationAudit(directory);
        var throwing = new ApprovalGate(policy, audit) { Approver = (_, _) => throw new InvalidOperationException("boom") };
        var failed = await throwing.AuthorizeAsync(new ActionRequest("net.post", RiskCategory.ExternalTransfer, "上传"));
        Assert(!failed.IsAllowed && failed.Decision == "approver-failed", "approver failure fails closed");

        var slow = new ApprovalGate(policy, audit)
        {
            ApprovalTimeout = TimeSpan.FromMilliseconds(200),
            Approver = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; }
        };
        var timedOut = await slow.AuthorizeAsync(new ActionRequest("net.post", RiskCategory.ExternalTransfer, "上传"));
        Assert(!timedOut.IsAllowed && timedOut.Decision == "timeout", "timeout fails closed");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelGate = new ApprovalGate(policy, audit) { Approver = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; } };
        var cancelledResult = await cancelGate.AuthorizeAsync(new ActionRequest("net.post", RiskCategory.ExternalTransfer, "上传"), cancelled.Token);
        Assert(!cancelledResult.IsAllowed && cancelledResult.Decision == "cancelled", "cancellation fails closed");
    }

    // 8. Every decision is audited; credentials are scrubbed; parameter values are never stored.
    {
        var audit = new OperationAudit(directory);
        var gate = new ApprovalGate(policy, audit) { Approver = (_, _) => Task.FromResult(true) };
        await gate.AuthorizeAsync(new ActionRequest(
            "net.post", RiskCategory.ExternalTransfer,
            "使用密钥 sk-abcdefgh12345678 上传文件",
            "https://example.com/upload",
            new Dictionary<string, string> { ["apiKey"] = "sk-abcdefgh12345678" }));

        var log = File.ReadAllText(audit.LogPath);
        Assert(!log.Contains("sk-abcdefgh12345678", StringComparison.Ordinal), "secret scrubbed from audit");
        Assert(log.Contains("apiKey", StringComparison.Ordinal), "parameter names retained");
        Assert(log.Contains("ExternalTransfer", StringComparison.Ordinal), "category recorded");

        var records = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert(records.Count >= 1 && records[^1].Decision == "allowed", "decision readable back");

        var exportPath = Path.Combine(directory, "export.jsonl");
        var exported = audit.Export(exportPath, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert(exported >= 1 && File.Exists(exportPath), "export writes records");

        audit.Clear();
        Assert(audit.Query(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).Count == 0, "clear removes the log");
    }

    // 9. A damaged line does not hide the healthy ones.
    {
        var audit = new OperationAudit(directory);
        var gate = new ApprovalGate(policy, audit);
        await gate.AuthorizeAsync(new ActionRequest("chat.read", RiskCategory.None, "读取"));
        File.AppendAllText(audit.LogPath, "{not json\n");
        await gate.AuthorizeAsync(new ActionRequest("chat.read", RiskCategory.None, "读取"));
        Assert(audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)).Count == 2, "damaged line skipped");
    }

    Console.WriteLine($"All {checks} permission checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}