using MyAiAssistant.Services.Bots;

namespace MyAiAssistant.Services.Messaging;

/// <summary>QQ through the official OpenClaw channel plugin (supports group chat).</summary>
public sealed class QQAdapter : IMessagingAdapter
{
    private readonly QQBotIntegration _integration;

    public QQAdapter(QQBotIntegration? integration = null) => _integration = integration ?? new QQBotIntegration();

    public MessagingPlatformInfo Info { get; } = new(
        PlatformKind.QQ,
        "QQ",
        ImplementationState.Implemented,
        new PlatformCapabilities(DirectMessage: true, GroupChat: true, Media: true),
        "OpenClaw + @tencent-connect/openclaw-qqbot 插件（QQ 开放平台机器人）");

    public async Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var status = await _integration.CheckAsync(cancellationToken).ConfigureAwait(false);
        return status.State switch
        {
            QQBotState.Ready => new(PlatformProbeState.Ready, status.Message),
            QQBotState.PluginMissing or QQBotState.CliMissing => new(PlatformProbeState.NeedsConfiguration, status.Message),
            _ => new(PlatformProbeState.Failed, status.Message)
        };
    }
}

/// <summary>
/// Every platform this build knows about. Only the ones with an adapter can be probed; the rest
/// are listed with the official mechanism that would be needed, and probe as Unavailable so no
/// surface can imply they work.
///
/// The list was checked against a mature multi-platform bot (AstrBot, AGPL-3.0). Only the shape of
/// the catalogue is borrowed — no code was copied, because AGPL is incompatible with this project's
/// MIT licence.
/// </summary>
public sealed class MessagingPlatformRegistry
{
    private static readonly MessagingPlatformInfo[] PlannedPlatforms =
    [
        new(PlatformKind.Discord, "Discord", ImplementationState.Planned,
            new(true, true, true), "Developer Portal 创建应用，Bot Token + Gateway intents（消息内容需特权 intent）"),

        new(PlatformKind.Feishu, "飞书 / Lark", ImplementationState.Planned,
            new(true, true, true), "开放平台自建应用 app_id/app_secret；事件优先走长连接，免公网 IP"),

        new(PlatformKind.DingTalk, "钉钉", ImplementationState.Planned,
            new(true, true, true), "群自定义机器人 Webhook（仅发），或应用凭证走 Stream 模式（可收）"),

        new(PlatformKind.Slack, "Slack", ImplementationState.Planned,
            new(true, true, true), "App 的 Bot Token（xoxb）+ Socket Mode，避免暴露公网端点"),

        new(PlatformKind.WeCom, "企业微信", ImplementationState.Planned,
            new(true, true, true), "官方 CLI：@wecom/cli 与 @wecom/wecom-openclaw-cli；群场景合规首选"),

        new(PlatformKind.WeComAiBot, "企业微信智能机器人", ImplementationState.Planned,
            new(true, true, true), "企业微信「智能机器人」回调：回调 URL + Token/EncodingAESKey"),

        new(PlatformKind.WeChat, "微信（个人）", ImplementationState.Planned,
            new(true, false, true), "两条路：官方 ClawBot(iLink) 需 token 且灰度；或第三方适配器（gewechat/wechatpadpro），后者有封号风险且需自行部署"),

        new(PlatformKind.WeChatOfficialAccount, "微信公众号", ImplementationState.Planned,
            new(false, false, true), "公众号后台开发者配置：AppID/AppSecret，服务器 URL + Token/EncodingAESKey"),


        new(PlatformKind.Kook, "KOOK", ImplementationState.Planned,
            new(true, true, true), "开发者后台创建机器人，Bot Token + WebSocket"),

        new(PlatformKind.Line, "LINE", ImplementationState.Planned,
            new(false, true, true), "Messaging API：Channel access token + Webhook"),

        new(PlatformKind.Mattermost, "Mattermost", ImplementationState.Planned,
            new(true, true, true), "自建实例：Bot 账号 + WebSocket 事件"),

        new(PlatformKind.Misskey, "Misskey", ImplementationState.Planned,
            new(true, true, true), "实例上创建 Bot 账号，Access Token + 流式 API"),

        new(PlatformKind.Satori, "Satori", ImplementationState.Planned,
            new(true, true, true), "Satori 协议服务端（可一次接入多平台），需自建或已有网关"),

        new(PlatformKind.WebChat, "网页内嵌对话", ImplementationState.Planned,
            new(true, false, true), "本机 WebSocket 服务，无需外部账号；适合先做通路验证")
    ];

    private readonly Dictionary<PlatformKind, IMessagingAdapter> _adapters;
    private readonly List<MessagingPlatformInfo> _platforms;

    public MessagingPlatformRegistry(IEnumerable<IMessagingAdapter>? adapters = null)
    {
        var provided = (adapters ?? Array.Empty<IMessagingAdapter>()).ToArray();
        _adapters = provided.ToDictionary(a => a.Info.Kind);
        _platforms = provided.Select(a => a.Info)
            .Concat(PlannedPlatforms)
            .OrderBy(p => p.Kind)
            .ToList();
    }

    public IReadOnlyList<MessagingPlatformInfo> Platforms => _platforms;

    public Task<PlatformProbeResult> ProbeAsync(PlatformKind kind, CancellationToken cancellationToken = default)
    {
        if (_adapters.TryGetValue(kind, out var adapter))
            return adapter.ProbeAsync(cancellationToken);

        var declared = _platforms.FirstOrDefault(p => p.Kind == kind);
        var requirement = declared?.Requirement ?? "未声明";
        return Task.FromResult(new PlatformProbeResult(
            PlatformProbeState.Unavailable,
            $"尚未实现。接入该平台需要：{requirement}"));
    }
}
