using MyAiAssistant.Services.Dashboard;

namespace DashboardTests;

/// <summary>A WebChat server that answers without opening a socket.</summary>
internal sealed class FakeWebChatControl : IWebChatControl
{
    private bool _running;

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    /// <summary>Simulates a port that is already taken.</summary>
    public bool RefuseStart { get; set; }

    public WebChatStatus Status() => new(_running, 9881, "ws://127.0.0.1:9881/", Clients: 0);

    public (bool Ok, string Message) Start()
    {
        StartCalls++;
        if (RefuseStart) return (false, "无法绑定端口 9881（AddressAlreadyInUse），端口可能已被占用。");

        _running = true;
        return (true, "WebChat 服务已启动：ws://127.0.0.1:9881/");
    }

    public (bool Ok, string Message) Stop()
    {
        StopCalls++;
        _running = false;
        return (true, "WebChat 服务已停止。");
    }
}
