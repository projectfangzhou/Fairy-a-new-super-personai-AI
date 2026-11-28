using System.Net.Sockets;
using MyAiAssistant.Services.Dashboard;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The desktop app's handle on the built-in web chat server: starts it, stops it, and reports its
/// state to the control centre. The dashboard only ever sees <see cref="IWebChatControl"/>, which
/// keeps the socket code out of the HTTP API's tests.
/// </summary>
public sealed class WebChatControl : IWebChatControl, IDisposable
{
    private readonly WebChatEndpoint _endpoint;
    private readonly int? _port;

    /// <param name="port">
    /// Overrides the port. Left null, a restart reuses the port the endpoint last listened on
    /// (<see cref="WebChatEndpoint.Port"/> keeps it across a stop), so a server started on a
    /// custom port is not silently moved back to the default one.
    /// </param>
    public WebChatControl(WebChatEndpoint endpoint, int? port = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _port = port;
    }

    public WebChatStatus Status() =>
        new(_endpoint.IsRunning, _endpoint.Port, _endpoint.Url, _endpoint.ClientCount);

    public (bool Ok, string Message) Start(int? port = null)
    {
        if (_endpoint.IsRunning) return (true, $"WebChat 服务已在运行：{_endpoint.Url}");

        var chosen = port ?? _port ?? _endpoint.Port;
        if (chosen is < 1 or > 65535)
            return (false, $"端口 {chosen} 不在 1-65535 范围内。");

        try
        {
            _endpoint.Start(chosen);
            return (true, $"WebChat 服务已启动：{_endpoint.Url}");
        }
        catch (SocketException ex)
        {
            return (false, $"无法绑定端口 {chosen}（{ex.SocketErrorCode}），换一个端口再试。");
        }
    }

    public (bool Ok, string Message) Stop()
    {
        if (!_endpoint.IsRunning) return (true, "WebChat 服务本来就没有运行。");
        _endpoint.Stop();
        return (true, "WebChat 服务已停止。");
    }

    public void Dispose() => _endpoint.Dispose();
}
