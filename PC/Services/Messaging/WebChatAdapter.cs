using System.Net.Sockets;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The built-in web chat surface: a loopback WebSocket that this machine hosts itself.
///
/// It is the only platform in the catalogue that needs no external account — no bot token, no
/// app registration, no network egress. That is exactly why it is worth having first: when
/// another adapter misbehaves, this one tells you whether the messaging path itself works or
/// whether the problem is the platform's credentials.
/// </summary>
public sealed class WebChatAdapter : IMessagingAdapter
{
    private readonly int _port;

    public WebChatAdapter(int? port = null) => _port = port ?? WebChatEndpoint.DefaultPort;

    public MessagingPlatformInfo Info { get; } = new(
        PlatformKind.WebChat,
        "网页内嵌对话",
        ImplementationState.Implemented,
        new PlatformCapabilities(DirectMessage: true, GroupChat: false, Media: false),
        "本机 WebSocket 服务，无需外部账号");

    public async Task<PlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = new WebChatEndpoint();

        try
        {
            endpoint.Start(_port);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
        {
            endpoint.Dispose();
            return new(PlatformProbeState.Failed,
                $"无法绑定本机端口 {_port}（{ex.GetType().Name}），端口可能已被其他程序占用。");
        }

        try
        {
            var result = await endpoint.SelfTestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Ok
                ? new(PlatformProbeState.Ready, result.Message)
                : new(PlatformProbeState.Failed, result.Message);
        }
        finally
        {
            endpoint.Dispose();
        }
    }
}
