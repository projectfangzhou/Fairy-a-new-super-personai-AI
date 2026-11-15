namespace MyAiAssistant.Services.Screen;

/// <summary>The pause/stop state as the control centre shows it.</summary>
public sealed record ScreenSessionView(
    string State,
    bool PausePending,
    int InFlight,
    int Completed,
    int Refused,
    string? LastRefusal,
    bool Supported,
    string Platform,
    string? UnsupportedReason);

public sealed record ScreenSessionAction(bool Ok, string Message);

/// <summary>
/// The three buttons the user needs. An interface so the dashboard can be tested without a real
/// session, and so nothing else in the app can reach into the session directly.
/// </summary>
public interface IScreenSessionControl
{
    ScreenSessionView Read();

    ScreenSessionAction Pause();

    ScreenSessionAction Resume();

    ScreenSessionAction Stop();
}

/// <summary>Wires the buttons to <see cref="ScreenSession"/> and reports the platform verdict.</summary>
public sealed class ScreenSessionControl : IScreenSessionControl
{
    private readonly ScreenSession _session;
    private readonly Func<ScreenControlAvailability> _availability;

    public ScreenSessionControl(ScreenSession session, Func<ScreenControlAvailability>? availability = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _availability = availability ?? ScreenControlAvailability.Detect;
    }

    public ScreenSessionView Read()
    {
        var availability = _availability();
        return new ScreenSessionView(
            _session.State.ToString(),
            _session.IsPausePending,
            _session.InFlightActions,
            _session.CompletedActions,
            _session.RefusedActions,
            _session.LastRefusal,
            availability.Supported,
            availability.Platform,
            availability.Reason);
    }

    public ScreenSessionAction Pause()
    {
        var state = _session.Pause();
        return state == ScreenSessionState.Stopped
            ? new ScreenSessionAction(false, "会话已停止，暂停没有意义；要重新开始请重启应用")
            : _session.IsPausePending
                ? new ScreenSessionAction(true, "已请求暂停：当前动作做完后生效，下一个动作不会执行")
                : new ScreenSessionAction(true, "已暂停，屏幕操作不会再执行");
    }

    public ScreenSessionAction Resume()
    {
        var state = _session.Resume();
        return state == ScreenSessionState.Stopped
            ? new ScreenSessionAction(false, "会话已停止，不能继续")
            : new ScreenSessionAction(true, "已继续，屏幕操作恢复");
    }

    public ScreenSessionAction Stop()
    {
        _session.Stop();
        return new ScreenSessionAction(true, "已停止：之后的屏幕操作一律不执行（继续也不会恢复）");
    }
}
