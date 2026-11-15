namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// Platforms this build knows about. The set follows what the major multi-platform bots cover,
/// so the catalogue does not pretend a platform does not exist just because it is not wired yet.
/// </summary>
public enum PlatformKind
{
    Telegram = 0,
    Discord,
    Feishu,
    DingTalk,
    Slack,
    WeCom,
    WeComAiBot,
    WeChat,
    WeChatOfficialAccount,
    QQ,
    QQOfficial,
    Kook,
    Line,
    Mattermost,
    Misskey,
    Satori,
    WebChat
}

/// <summary>
/// Whether this build can actually talk to the platform. Keeping this explicit is what stops a
/// listed-but-unbuilt platform from looking supported.
/// </summary>
public enum ImplementationState
{
    Implemented = 0,
    Planned
}

public sealed record PlatformCapabilities(bool DirectMessage, bool GroupChat, bool Media);

/// <summary>
/// One platform's declaration. <paramref name="Requirement"/> names the official mechanism the
/// user has to obtain (a bot token, an app id/secret, a third-party adapter…), so nobody has to
/// guess what is missing.
/// </summary>
public sealed record MessagingPlatformInfo(
    PlatformKind Kind,
    string DisplayName,
    ImplementationState State,
    PlatformCapabilities Capabilities,
    string Requirement);

public enum PlatformProbeState
{
    Ready = 0,
    NeedsConfiguration,
    Unavailable,
    Failed
}

public sealed record PlatformProbeResult(PlatformProbeState State, string Message)
{
    public bool IsReady => State == PlatformProbeState.Ready;
}

public interface IMessagingAdapter
{
    MessagingPlatformInfo Info { get; }

    /// <summary>Reports the true state; it must never report Ready without evidence.</summary>
    Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}