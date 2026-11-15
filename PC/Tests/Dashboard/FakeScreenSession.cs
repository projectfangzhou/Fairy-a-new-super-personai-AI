using MyAiAssistant.Services.Screen;

namespace DashboardTests;

/// <summary>
/// A screen session control backed by the real <see cref="ScreenSession"/>, so the dashboard tests
/// exercise the actual pause/stop semantics instead of a hand-written stub.
/// </summary>
internal sealed class FakeScreenSessionControl : IScreenSessionControl
{
    private readonly ScreenSession _session = new();

    public bool Supported { get; init; } = true;

    public MyAiAssistant.Services.Screen.ScreenSessionView Read() => new(
        _session.State.ToString(),
        _session.IsPausePending,
        _session.InFlightActions,
        _session.CompletedActions,
        _session.RefusedActions,
        _session.LastRefusal,
        Supported,
        Supported ? "windows" : "linux(wayland/gnome)",
        Supported ? null : "尚未在目标发行版验证");

    public MyAiAssistant.Services.Screen.ScreenSessionAction Pause()
    {
        var state = _session.Pause();
        return new MyAiAssistant.Services.Screen.ScreenSessionAction(true,
            state == ScreenSessionState.Paused ? "已暂停" : "已请求暂停");
    }

    public MyAiAssistant.Services.Screen.ScreenSessionAction Resume()
    {
        var state = _session.Resume();
        return new MyAiAssistant.Services.Screen.ScreenSessionAction(state != ScreenSessionState.Stopped,
            state == ScreenSessionState.Stopped ? "已停止，不能继续" : "已继续");
    }

    public MyAiAssistant.Services.Screen.ScreenSessionAction Stop()
    {
        _session.Stop();
        return new MyAiAssistant.Services.Screen.ScreenSessionAction(true, "已停止");
    }
}
