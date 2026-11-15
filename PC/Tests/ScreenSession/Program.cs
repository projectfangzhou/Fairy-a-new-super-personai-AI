using MyAiAssistant.Services;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-screen-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ---------------------------------------------------------------- the state machine

    {
        var session = new ScreenSession();
        Assert(session.State == ScreenSessionState.Running, "a session starts running");

        Assert(session.Pause() == ScreenSessionState.Paused, "pausing with nothing in flight applies at once");
        Assert(session.BeginAction().Allowed == false, "a paused session refuses the next action");
        Assert(session.LastRefusal == ScreenRefusal.Paused, "and says it is paused");

        Assert(session.Resume() == ScreenSessionState.Running, "resume returns to running");
        Assert(session.BeginAction().Allowed, "an action is allowed after resuming");
        session.EndAction();

        session.Stop();
        Assert(session.State == ScreenSessionState.Stopped, "stop is immediate");
        Assert(session.BeginAction().Allowed == false, "a stopped session refuses actions");
        Assert(session.LastRefusal == ScreenRefusal.Stopped, "and says it stopped");
        Assert(session.Resume() == ScreenSessionState.Stopped, "resume does not resurrect a stopped session");
        Assert(session.BeginAction().Allowed == false, "still refused after a resume attempt");
    }

    {
        // A pause that arrives mid-action must not cut the action in half — it takes effect at the
        // boundary, before the next one.
        var session = new ScreenSession();
        Assert(session.BeginAction().Allowed, "first action allowed");

        var stateDuringPause = session.Pause();
        Assert(stateDuringPause == ScreenSessionState.Running, "while an action runs the state stays running");
        Assert(session.IsPausePending, "but the pause is remembered");

        session.EndAction();
        Assert(session.State == ScreenSessionState.Paused, "the pause lands when the action finishes");
        Assert(!session.IsPausePending, "and is no longer pending");
        Assert(session.BeginAction().Allowed == false, "the next action is refused");
    }

    {
        var session = new ScreenSession();
        session.BeginAction();
        session.Stop();
        session.EndAction();
        Assert(session.State == ScreenSessionState.Stopped, "stop survives the action finishing");
        Assert(session.BeginAction().Allowed == false, "nothing runs after a stop");
    }

    // ---------------------------------------------------------------- through the gate

    ScreenActionRunner Runner(FakeScreen screen, ScreenSession session, ApprovalGate gate,
        Func<ActionRequest, CancellationToken, Task<bool>>? approver = null,
        bool supported = true) =>
        new(screen, session, () => gate, approver,
            () => supported
                ? new ScreenControlAvailability(true, "windows", null)
                : new ScreenControlAvailability(false, "linux(x11/gnome)", "尚未验证"));

    {
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var audit = new OperationAudit(Path.Combine(directory, "approved"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var asked = 0;
        var runner = Runner(screen, session, gate, (_, _) => { asked++; return Task.FromResult(true); });

        var result = await runner.RunAsync("mouse_click", "点击屏幕 (10, 20)", "screen:10,20",
            s => { s.Click(10, 20); return Task.FromResult("Clicked at (10, 20)"); });

        Assert(result.Performed, "an approved action runs");
        Assert(result.Message == "Clicked at (10, 20)", "the tool's message is returned");
        Assert(asked == 1, "the user was asked exactly once");
        Assert(screen.Calls.Count == 1 && screen.Calls[0] == "click 10,20", "the action reached the service");
        Assert(session.CompletedActions == 1 && session.InFlightActions == 0, "the session counted it and cleared");

        var records = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert(records.Any(r => r.Category == RiskCategory.OperateScreenAndInput),
            "the audit records the screen category");
        Assert(records.Any(r => r.Decision == "allowed"), "and that it was allowed");
    }

    {
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "denied")));
        var runner = Runner(screen, session, gate, (_, _) => Task.FromResult(false));

        var result = await runner.RunAsync("mouse_click", "点击屏幕 (10, 20)", null,
            s => { s.Click(10, 20); return Task.FromResult("clicked"); });

        Assert(!result.Performed, "a refused action does not run");
        Assert(result.Message.Contains("未执行", StringComparison.Ordinal), "the message says it was not executed");
        Assert(screen.Calls.Count == 0, "the service was never touched");
        Assert(session.CompletedActions == 0, "and nothing was counted as completed");
    }

    {
        // Fail closed: no approver means no action, not a default yes.
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "noapprover")));
        var runner = Runner(screen, session, gate);

        var result = await runner.RunAsync("type_text", "输入 3 个字符", null,
            s => { s.TypeText("abc"); return Task.FromResult("typed"); });

        Assert(!result.Performed && screen.Calls.Count == 0, "without an approver nothing happens");
    }

    {
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "paused")));
        var asked = 0;
        var runner = Runner(screen, session, gate, (_, _) => { asked++; return Task.FromResult(true); });

        session.Pause();
        var result = await runner.RunAsync("mouse_click", "点击", null,
            s => { s.Click(1, 1); return Task.FromResult("clicked"); });

        Assert(!result.Performed, "a paused session performs nothing");
        Assert(result.Message.Contains("暂停", StringComparison.Ordinal), "and says it is paused");
        Assert(asked == 0, "a paused session does not even ask the user");
        Assert(session.RefusedActions == 1, "the refusal is counted");

        session.Stop();
        var afterStop = await runner.RunAsync("mouse_click", "点击", null,
            s => { s.Click(1, 1); return Task.FromResult("clicked"); });
        Assert(!afterStop.Performed && afterStop.Message.Contains("停止", StringComparison.Ordinal),
            "a stopped session refuses with the stop reason");
        Assert(screen.Calls.Count == 0, "nothing reached the service in either case");
    }

    {
        // Unsupported platform: refuse with the reason, and do not bother the user with a prompt.
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "linux")));
        var asked = 0;
        var runner = Runner(screen, session, gate, (_, _) => { asked++; return Task.FromResult(true); }, supported: false);

        var result = await runner.RunAsync("mouse_click", "点击", null,
            s => { s.Click(1, 1); return Task.FromResult("clicked"); });

        Assert(!result.Performed, "an unsupported platform performs nothing");
        Assert(result.Message.Contains("不支持", StringComparison.Ordinal), "the message explains the platform");
        Assert(asked == 0, "and the user is not asked about an action that cannot run");
    }

    {
        // Cancellation is a refusal, and the session is released rather than left stuck in flight.
        var screen = new FakeScreen();
        var session = new ScreenSession();
        var gate = new ApprovalGate(new PermissionPolicy(), new OperationAudit(Path.Combine(directory, "cancel")));
        var runner = Runner(screen, session, gate, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await runner.RunAsync("mouse_click", "点击", null,
            s => { s.Click(1, 1); return Task.FromResult("clicked"); }, cancellation.Token);

        Assert(!result.Performed, "a cancelled approval performs nothing");
        Assert(session.InFlightActions == 0, "the session is not left with an action in flight");
    }

    {
        // Availability detection itself: Windows is supported, Linux explains why it is not.
        var windows = ScreenControlAvailability.Detect(isWindows: true, isLinux: false, sessionType: null, desktop: null);
        Assert(windows.Supported, "Windows is supported");

        var linux = ScreenControlAvailability.Detect(isWindows: false, isLinux: true, sessionType: "wayland", desktop: "GNOME");
        Assert(!linux.Supported, "Linux is disabled until verified");
        Assert(linux.Reason!.Contains("验证", StringComparison.Ordinal), "and the reason says so");
        Assert(linux.Platform.Contains("wayland", StringComparison.Ordinal), "the reported platform names the session");

        var other = ScreenControlAvailability.Detect(isWindows: false, isLinux: false, sessionType: null, desktop: null);
        Assert(!other.Supported && other.Reason is not null, "an unknown platform is disabled with a reason");
    }

    {
        // The three buttons, through the control the dashboard uses.
        var session = new ScreenSession();
        var control = new ScreenSessionControl(session, () => new ScreenControlAvailability(true, "windows", null));

        var initial = control.Read();
        Assert(initial.State == "Running" && initial.Supported, "the view starts running and supported");

        var paused = control.Pause();
        Assert(paused.Ok && paused.Message.Contains("已暂停", StringComparison.Ordinal), "pause reports the new state");
        Assert(control.Read().State == "Paused", "and the view agrees");

        Assert(control.Resume().Ok, "resume succeeds from paused");
        Assert(control.Read().State == "Running", "and the view agrees again");

        // Pause while an action is in flight: the message must say "after the current action".
        session.BeginAction();
        var pending = control.Pause();
        Assert(pending.Ok && pending.Message.Contains("做完后生效", StringComparison.Ordinal),
            "a mid-action pause explains that it lands after the current action");
        session.EndAction();
        Assert(control.Read().State == "Paused", "and it did land");

        var stopped = control.Stop();
        Assert(stopped.Ok && stopped.Message.Contains("一律不执行", StringComparison.Ordinal), "stop says it is final");
        Assert(!control.Resume().Ok, "resuming a stopped session reports failure");
        Assert(!control.Pause().Ok, "pausing a stopped session is pointless and says so");
        Assert(control.Read().State == "Stopped", "the session stays stopped");
    }

    Console.WriteLine($"All {checks} screen session checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}

internal sealed class FakeScreen : IScreenControlService
{
    public List<string> Calls { get; } = new();

    public void MoveTo(int x, int y) => Calls.Add($"move {x},{y}");
    public void Click(int x, int y) => Calls.Add($"click {x},{y}");
    public void DoubleClick(int x, int y) => Calls.Add($"double {x},{y}");
    public void RightClick(int x, int y) => Calls.Add($"right {x},{y}");
    public void Scroll(int delta) => Calls.Add($"scroll {delta}");
    public void TypeText(string text) => Calls.Add($"type {text}");
    public void PressKey(string key) => Calls.Add($"key {key}");
    public (int Width, int Height) GetScreenSize() => (1920, 1080);
}
