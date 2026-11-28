using MyAiAssistant.Services.Dashboard;

namespace DashboardTests;

/// <summary>A WebChat server that answers without opening a socket.</summary>
internal sealed class FakeWebChatControl : IWebChatControl
{
    private bool _running;
    private int _port = 9881;

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    /// <summary>The port the last start asked for; null means "whatever it was".</summary>
    public int? LastRequestedPort { get; private set; }

    /// <summary>Simulates a port that is already taken.</summary>
    public bool RefuseStart { get; set; }

    public WebChatStatus Status() => new(_running, _port, $"ws://127.0.0.1:{_port}/", Clients: 0);

    public (bool Ok, string Message) Start(int? port = null)
    {
        StartCalls++;
        LastRequestedPort = port;
        if (RefuseStart) return (false, $"无法绑定端口 {port ?? _port}（AddressAlreadyInUse），换一个端口再试。");

        if (port is > 0 and <= 65535) _port = port.Value;
        _running = true;
        return (true, $"WebChat 服务已启动：ws://127.0.0.1:{_port}/");
    }

    public (bool Ok, string Message) Stop()
    {
        StopCalls++;
        _running = false;
        return (true, "WebChat 服务已停止。");
    }
}
