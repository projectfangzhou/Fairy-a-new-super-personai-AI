namespace MyAiAssistant.Services.Screen;

/// <summary>Why a screen action was not performed. These strings reach the user, so they say what to do.</summary>
public static class ScreenRefusal
{
    public const string Stopped = "会话已停止，不再执行任何屏幕操作";
    public const string Paused = "会话已暂停，继续后才会执行";
    public const string NotAvailable = "当前环境不支持屏幕操作";

    public static string Denied(string reason) => $"未执行：{reason}";

    public static string Unsupported(string reason) => $"{NotAvailable}：{reason}";
}

public enum ScreenSessionState
{
    Running = 0,
    Paused,
    Stopped
}

/// <summary>The answer to "may this one action run now?"</summary>
public sealed record ScreenActionPermit(bool Allowed, string? Reason)
{
    public static ScreenActionPermit Allow() => new(true, null);
    public static ScreenActionPermit Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The pause / resume / stop state of one screen-control session.
///
/// The rule that matters is the boundary: a pause requested while an action is running must not
/// abort that action — half a click is worse than a whole one — but it must stop the next action.
/// So a pause either applies immediately (nothing in flight) or is remembered and applied when the
/// current action finishes. A stop applies immediately and is final: nothing new runs afterwards,
/// and the action already in flight is allowed to finish for the same reason.
///
/// This class holds no OS handles and no clock, which is what makes the semantics testable.
/// </summary>
public sealed class ScreenSession
{
    private readonly object _sync = new();
    private ScreenSessionState _state = ScreenSessionState.Running;
    private int _inFlight;
    private bool _pausePending;
    private int _completed;
    private int _refused;
    private string? _lastRefusal;

    public ScreenSessionState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    /// <summary>True when a pause was requested while an action was still running.</summary>
    public bool IsPausePending
    {
        get
        {
            lock (_sync) return _pausePending;
        }
    }

    public int InFlightActions
    {
        get
        {
            lock (_sync) return _inFlight;
        }
    }

    public int CompletedActions
    {
        get
        {
            lock (_sync) return _completed;
        }
    }

    public int RefusedActions
    {
        get
        {
            lock (_sync) return _refused;
        }
    }

    /// <summary>Why the last action was refused. Shown in the control centre.</summary>
    public string? LastRefusal
    {
        get
        {
            lock (_sync) return _lastRefusal;
        }
    }

    /// <summary>
    /// Requests a pause. Returns the state right after the call: <see cref="ScreenSessionState.Paused"/>
    /// when nothing was running, otherwise still running with <see cref="IsPausePending"/> set.
    /// A pause request has no effect on a stopped session.
    /// </summary>
    public ScreenSessionState Pause()
    {
        lock (_sync)
        {
            if (_state == ScreenSessionState.Stopped) return _state;

            if (_inFlight == 0)
            {
                _state = ScreenSessionState.Paused;
                _pausePending = false;
                return _state;
            }

            _pausePending = true;
            return _state;
        }
    }

    public ScreenSessionState Resume()
    {
        lock (_sync)
        {
            // A stopped session does not resume: stop means stop, not "stop until someone resumes".
            if (_state == ScreenSessionState.Stopped) return _state;

            _state = ScreenSessionState.Running;
            _pausePending = false;
            return _state;
        }
    }

    public ScreenSessionState Stop()
    {
        lock (_sync)
        {
            _state = ScreenSessionState.Stopped;
            _pausePending = false;
            return _state;
        }
    }

    /// <summary>Asks to start one atomic action. A refusal is recorded so the UI can explain it.</summary>
    public ScreenActionPermit BeginAction()
    {
        lock (_sync)
        {
            if (_state == ScreenSessionState.Stopped) return RefuseLocked(ScreenRefusal.Stopped);
            if (_state == ScreenSessionState.Paused) return RefuseLocked(ScreenRefusal.Paused);

            _inFlight++;
            return ScreenActionPermit.Allow();
        }
    }

    /// <summary>
    /// Marks the atomic action finished, and applies a pending pause now that nothing is in flight.
    /// Must be called exactly once per allowed <see cref="BeginAction"/>.
    /// </summary>
    public void EndAction(bool succeeded = true)
    {
        lock (_sync)
        {
            if (_inFlight > 0) _inFlight--;
            if (succeeded) _completed++;

            if (_inFlight == 0 && _pausePending && _state == ScreenSessionState.Running)
            {
                _state = ScreenSessionState.Paused;
                _pausePending = false;
            }
        }
    }

    private ScreenActionPermit RefuseLocked(string reason)
    {
        _refused++;
        _lastRefusal = reason;
        return ScreenActionPermit.Refuse(reason);
    }
}
