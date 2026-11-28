namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The built-in web chat surface: a loopback WebSocket that this machine hosts itself.
///
/// It is the only platform in the catalogue that needs no external account — no bot token, no
/// app registration, no network egress. The probe reports readiness only when the server is
/// actually running and answers a ping round trip; a stopped server is reported as needing to be
/// started, because a throwaway socket test would say nothing about whether chat works.
/// </summary>
public sealed class WebChatAdapter : IMessagingAdapter
{
    private readonly WebChatEndpoint? _endpoint;
    private readonly int _port;

    public WebChatAdapter(WebChatEndpoint? endpoint = null, int? port = null)
    {
        _endpoint = endpoint;
        _port = port ?? WebChatEndpoint.DefaultPort;
    }

    public MessagingPlatformInfo Info { get; } = new(
        PlatformKind.WebChat,
        "网页内嵌对话",
        ImplementationState.Implemented,
        new PlatformCapabilities(DirectMessage: true, GroupChat: false, Media: false),
        "本机 WebSocket 服务，无需外部账号");

    public async Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint is null || !_endpoint.IsRunning)
            return new(PlatformProbeState.NeedsConfiguration,
                $"WebChat 服务未启动。启动后本机客户端可连接 ws://127.0.0.1:{_port}/ 对话。");

        // Read the count before the probe connects: the probe's own socket is a client too, and
        // whether its teardown has been accounted for by the time the message is built is a race.
        // Reading first makes "no clients" mean no clients, whatever the scheduler does.
        var clients = _endpoint.ClientCount;

        var result = await _endpoint.SelfTestAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok) return new(PlatformProbeState.Failed, result.Message);

        return new(PlatformProbeState.Ready,
            clients == 0 ? result.Message : $"{result.Message}当前 {clients} 个连接。");
    }
}
