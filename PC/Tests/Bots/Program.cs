using MyAiAssistant.Services.Bots;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var tempDir = Path.Combine(Path.GetTempPath(), "fairyai-bots-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var fakeCliPath = Path.Combine(tempDir, "openclaw.cmd");
File.WriteAllText(fakeCliPath, "@echo off");

try
{
    // 1. No CLI on disk => tell the user to install OpenClaw.
    {
        var cli = new FakeCli();
        var integration = new QQBotIntegration(cli, new[] { Path.Combine(tempDir, "missing.cmd") });
        var status = await integration.CheckAsync();
        Assert(status.State == QQBotState.CliMissing, "missing cli reported");
        Assert(status.Message.Contains("npm i -g openclaw", StringComparison.Ordinal), "install hint given");
        Assert(cli.Calls.Count == 0, "no process launched without a cli");
    }

    // 2. A CLI that exists but cannot run is broken, not usable.
    {
        var cli = new FakeCli { Respond = _ => new CliResult(false, 1, "Error: Cannot find module 'openclaw.mjs'") };
        var integration = new QQBotIntegration(cli, new[] { fakeCliPath });
        var status = await integration.CheckAsync();
        Assert(status.State == QQBotState.CliBroken, "broken cli detected");
        Assert(status.Message.Contains("openclaw.mjs", StringComparison.Ordinal), "real error surfaced");
    }

    // 3. `plugins list` failing is also a broken install.
    {
        var cli = new FakeCli
        {
            Respond = args => args == "--version"
                ? new CliResult(true, 0, "1.0.0")
                : new CliResult(false, 1, "boom")
        };
        var status = await new QQBotIntegration(cli, new[] { fakeCliPath }).CheckAsync();
        Assert(status.State == QQBotState.CliBroken, "plugins list failure detected");
    }

    // 4. Working CLI without the plugin => give the exact install command and group notes.
    {
        var cli = new FakeCli
        {
            Respond = args => args == "--version"
                ? new CliResult(true, 0, "1.0.0")
                : new CliResult(true, 0, "no plugins installed")
        };
        var status = await new QQBotIntegration(cli, new[] { fakeCliPath }).CheckAsync();
        Assert(status.State == QQBotState.PluginMissing, "missing plugin reported");
        Assert(status.Message.Contains("@tencent-connect/openclaw-qqbot", StringComparison.Ordinal), "package named");
        Assert(status.Message.Contains("/bot-group-always", StringComparison.Ordinal), "group chat mode explained");
        Assert(status.Message.Contains("requireMention", StringComparison.Ordinal), "per-group rule explained");
    }

    // 5. Plugin present => ready, and group chat remains documented.
    {
        var cli = new FakeCli
        {
            Respond = args => args == "--version"
                ? new CliResult(true, 0, "1.0.0")
                : new CliResult(true, 0, "@tencent-connect/openclaw-qqbot  2.0.4  enabled")
        };
        var status = await new QQBotIntegration(cli, new[] { fakeCliPath }).CheckAsync();
        Assert(status.State == QQBotState.Ready, "installed plugin reported ready");
        Assert(status.Message.Contains("群聊", StringComparison.Ordinal), "group chat guidance kept");
    }

    // 6. Installing without an approver must not run anything.
    {
        var audit = new OperationAudit(Path.Combine(tempDir, "audit1"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var cli = new FakeCli
        {
            Respond = args => args == "--version" ? new CliResult(true, 0, "1.0.0") : new CliResult(true, 0, "none")
        };
        var result = await new QQBotIntegration(cli, new[] { fakeCliPath }).InstallPluginAsync(gate);
        Assert(result.State == BotSetupState.Failed, "install refused without approval");
        Assert(!cli.Calls.Any(c => c.Contains("install", StringComparison.Ordinal)), "install never executed");
    }

    // 7. An approved install runs the official command and re-checks the result.
    {
        var audit = new OperationAudit(Path.Combine(tempDir, "audit2"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit) { Approver = (_, _) => Task.FromResult(true) };
        var installed = false;
        var cli = new FakeCli
        {
            Respond = args =>
            {
                if (args == "--version") return new CliResult(true, 0, "1.0.0");
                if (args.Contains("plugins install", StringComparison.Ordinal))
                {
                    installed = true;
                    return new CliResult(true, 0, "added 1 plugin");
                }
                return installed
                    ? new CliResult(true, 0, "@tencent-connect/openclaw-qqbot enabled")
                    : new CliResult(true, 0, "none");
            }
        };

        var result = await new QQBotIntegration(cli, new[] { fakeCliPath }).InstallPluginAsync(gate);
        Assert(result.State == BotSetupState.Ready, "approved install ends ready");
        Assert(cli.Calls.Any(c => c == "plugins install @tencent-connect/openclaw-qqbot@latest"), "official install command used");
        Assert(audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5))
            .Any(r => r.ActionId == "bot.qq.install" && r.Category == RiskCategory.InstallOrRunUnverifiedCode),
            "install recorded as a high-risk audited action");
    }

    // 8. Install on a broken CLI is refused before any approval prompt.
    {
        var audit = new OperationAudit(Path.Combine(tempDir, "audit3"));
        var prompted = 0;
        var gate = new ApprovalGate(new PermissionPolicy(), audit)
        {
            Approver = (_, _) => { prompted++; return Task.FromResult(true); }
        };
        var cli = new FakeCli { Respond = _ => new CliResult(false, 1, "MODULE_NOT_FOUND") };
        var result = await new QQBotIntegration(cli, new[] { fakeCliPath }).InstallPluginAsync(gate);
        Assert(result.State == BotSetupState.Failed, "broken cli blocks install");
        Assert(prompted == 0, "no pointless prompt on a broken install");
        Assert(cli.Calls.All(c => c == "--version"), "nothing installed");
    }

    Console.WriteLine($"All {checks} bot integration checks passed.");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch (IOException) { }
}

// A fake CLI: records invocations and replays scripted answers.
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
