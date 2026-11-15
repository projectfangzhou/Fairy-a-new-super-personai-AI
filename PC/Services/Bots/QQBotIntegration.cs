using System.IO;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services.Bots;

public enum QQBotState
{
    /// <summary>Plugin installed and the CLI answers; the bot can be started.</summary>
    Ready,

    /// <summary>OpenClaw works but the QQ plugin is not installed.</summary>
    PluginMissing,

    /// <summary>No OpenClaw CLI could be located.</summary>
    CliMissing,

    /// <summary>An OpenClaw CLI exists but cannot run (broken install).</summary>
    CliBroken,

    /// <summary>Unexpected failure.</summary>
    Failed
}

public sealed record QQBotStatus(QQBotState State, string Message, string? CliPath = null);

/// <summary>
/// QQ bot through the official OpenClaw plugin
/// <c>@tencent-connect/openclaw-qqbot</c> (MIT, Tencent Connect).
///
/// That plugin is the reason this route was chosen: it natively supports group chat with a
/// per-group @-mention rule and an autonomous mode, which is a hard requirement here. It is an
/// OpenClaw channel plugin, so it reuses the OpenClaw runtime the app already ships.
///
/// Detection never trusts the presence of a file: it asks the CLI to run, because a half-removed
/// install (openclaw.cmd without openclaw.mjs) looks installed but answers nothing.
/// </summary>
public sealed class QQBotIntegration
{
    public const string PackageName = "@tencent-connect/openclaw-qqbot";
    /// <summary>Version observed on the registry when this integration was written.</summary>
    public const string KnownVersion = "2.0.4";

    private static readonly string[] CliCandidates =
    [
        @"D:\npm-global\openclaw.cmd",
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openclaw.cmd"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "openclaw.cmd")
    ];

    private readonly ICliRunner _runner;
    private readonly IReadOnlyList<string> _candidates;

    public QQBotIntegration(ICliRunner? runner = null, IEnumerable<string>? candidates = null)
    {
        _runner = runner ?? new ProcessCliRunner();
        _candidates = (candidates ?? CliCandidates).ToArray();
    }

    public async Task<QQBotStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var cli = _candidates.FirstOrDefault(File.Exists);
        if (cli is null)
        {
            return new(QQBotState.CliMissing,
                "未找到 OpenClaw 命令行。QQ 走官方 OpenClaw 插件，需要先安装 OpenClaw：\n  npm i -g openclaw",
                null);
        }

        var version = await _runner.RunAsync(cli, "--version", TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        if (!version.Ok)
        {
            return new(QQBotState.CliBroken,
                $"OpenClaw 已安装但无法运行，需要先修复或重装：\n  {cli}\n\n" + Tail(version.Output),
                cli);
        }

        var plugins = await _runner.RunAsync(cli, "plugins list", TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (!plugins.Ok)
        {
            return new(QQBotState.CliBroken,
                "`openclaw plugins list` 执行失败，无法确认插件状态：\n\n" + Tail(plugins.Output), cli);
        }

        var installed = plugins.OutputContains(PackageName) || plugins.OutputContains("qqbot");
        if (!installed)
        {
            return new(QQBotState.PluginMissing,
                "OpenClaw 可用，但 QQ 插件未安装：\n" +
                $"  openclaw plugins install {PackageName}@latest\n\n" + GroupChatNotes,
                cli);
        }

        return new(QQBotState.Ready,
            "QQ 插件已安装。启动 OpenClaw 后即可使用。\n\n" + GroupChatNotes,
            cli);
    }

    /// <summary>
    /// Installs the plugin, but only after the user approves it: installing a dependency is a
    /// high-risk category, so it must pass the same gate as any other such action.
    /// </summary>
    public async Task<BotSetupResult> InstallPluginAsync(ApprovalGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        var status = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is QQBotState.CliMissing or QQBotState.CliBroken)
            return new(BotSetupState.Failed, status.Message);

        var approval = await gate.AuthorizeAsync(new ActionRequest(
            "bot.qq.install",
            RiskCategory.InstallOrRunUnverifiedCode,
            "安装 QQ Bot 插件（来自 npm 的官方包）",
            PackageName), cancellationToken).ConfigureAwait(false);

        if (!approval.IsAllowed)
            return new(BotSetupState.Failed, $"未获批准，未执行安装（{approval.Decision}：{approval.Reason}）。");

        var cli = status.CliPath!;
        var result = await _runner.RunAsync(cli, $"plugins install {PackageName}@latest",
            TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
            return new(BotSetupState.Failed, $"安装失败：\n\n{Tail(result.Output)}");

        var after = await CheckAsync(cancellationToken).ConfigureAwait(false);
        return after.State == QQBotState.Ready
            ? new(BotSetupState.Ready, "QQ 插件安装完成。\n\n" + GroupChatNotes)
            : new(BotSetupState.ManualStepRequired, "安装命令已执行，但仍未确认插件就位：\n" + after.Message);
    }

    /// <summary>Group chat facts verified against the plugin's own README.</summary>
    public const string GroupChatNotes =
        "群聊说明：\n" +
        "· 支持私聊与群聊；群聊默认需要 @机器人 才回复。\n" +
        "· 在群里发送 /bot-group-always on 可切换为自主发言（不 @ 也回），off 恢复 @ 触发。\n" +
        "· 也可以按群单独配置：groups.{群ID}.requireMention，或用 groups.\"*\" 对所有群生效。\n" +
        "· 插件同时支持 WebSocket（免公网 IP）与 Webhook 两种接入方式。";

    private static string Tail(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "(无输出)";
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(6)).Trim();
    }
}