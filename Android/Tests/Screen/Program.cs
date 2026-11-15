using FairyAI_Android.Services.Performance;
using FairyAI_Android.Services.Screen;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

ScreenGate Gate(
    IShizukuBridge? bridge,
    IScreenApproval? approval = null,
    IEnumerable<ScreenCapability>? grants = null,
    bool enabled = true,
    Action<string>? audit = null) =>
    new(bridge, approval,
        () => new HashSet<ScreenCapability>(grants ?? Array.Empty<ScreenCapability>()),
        () => enabled,
        audit);

var authorized = new ShizukuProbeResult(ShizukuAvailability.Authorized, "13.1.5", "ok");
var notInstalled = new ShizukuProbeResult(ShizukuAvailability.NotInstalled, null, "missing");
var unbound = new ShizukuProbeResult(ShizukuAvailability.BridgeUnbound, null, "no sdk binding");
var notAuthorized = new ShizukuProbeResult(ShizukuAvailability.NotAuthorized, null, "no grant");
var notRunning = new ShizukuProbeResult(ShizukuAvailability.NotRunning, null, "service down");

var allGranted = ScreenCapabilityCatalog.All.ToArray();

// ---------------------------------------------------------------- whitelist

Assert(ScreenCapabilityCatalog.All.Count == 5, "whitelist holds exactly the five screen capabilities");
Assert(ScreenCapabilityCatalog.IsWhitelisted(ScreenCapability.Tap), "tap is whitelisted");
Assert(ScreenCapabilityCatalog.Parse("TaP") == ScreenCapability.Tap, "capability names parse case-insensitively");
Assert(ScreenCapabilityCatalog.Parse("InstallPackage") is null, "a capability outside the whitelist does not parse");
Assert(ScreenCapabilityCatalog.Parse(null) is null, "null parses to nothing");
Assert(ScreenCapabilityCatalog.ParseAll(new[] { "Tap", "Nonsense", "ReadScreen" }).Count == 2,
    "unknown names in stored config are dropped, not coerced");
Assert(ScreenCapabilityCatalog.Label(ScreenCapability.TypeText) == "输入文字", "labels are human readable");

// ------------------------------------------------------------ target policy

Assert(ScreenTargetPolicy.IsBlockedPackage("com.eg.android.AlipayGphone"), "Alipay is a blocked target");
Assert(ScreenTargetPolicy.IsBlockedPackage("  com.icbc  "), "blocked package matching trims whitespace");
Assert(!ScreenTargetPolicy.IsBlockedPackage("com.tencent.mm"), "WeChat is deliberately not blocked");
Assert(!ScreenTargetPolicy.IsBlockedPackage(null), "null package is not blocked");
Assert(ScreenTargetPolicy.ContainsSensitiveAction("帮我转账 100 元"), "Chinese payment wording is caught");
Assert(ScreenTargetPolicy.ContainsSensitiveAction("enter the PASSWORD"), "English payment wording is caught");
Assert(!ScreenTargetPolicy.ContainsSensitiveAction("明天天气怎么样"), "ordinary text is not caught");

// --------------------------------------------------------------- the gate

var blockedBridge = new FakeBridge();
var blockedDecision = Gate(blockedBridge, null, allGranted).Inspect(ScreenCapability.Tap, "com.icbc");
Assert(!blockedDecision.Allowed && blockedDecision.Reason == ScreenRefusal.TargetBlocked,
    "a financial app is refused before anything else is considered");
Assert(blockedBridge.Injections == 0, "a blocked target never reaches the bridge");

var unboundProbe = new FakeBridge { Result = unbound };
var unboundDecision = Gate(unboundProbe, null, allGranted).Inspect(ScreenCapability.Tap, "com.tencent.mm");
Assert(!unboundDecision.Allowed && unboundDecision.Reason == ScreenRefusal.BridgeUnbound,
    "an unbound build says so instead of pretending");

var installedDecision = Gate(new FakeBridge { Result = notInstalled }, null, allGranted)
    .Inspect(ScreenCapability.Tap);
Assert(installedDecision.Reason == ScreenRefusal.NotInstalled, "missing Shizuku is reported as not installed");

var notAuthorizedDecision = Gate(new FakeBridge { Result = notAuthorized }, null, allGranted)
    .Inspect(ScreenCapability.Tap);
Assert(notAuthorizedDecision.Reason == ScreenRefusal.NotAuthorized
       && notAuthorizedDecision.Message.Contains("授权", StringComparison.Ordinal),
    "an unauthorized Shizuku refusal explains the authorization step");

var notRunningDecision = Gate(new FakeBridge { Result = notRunning }, null, allGranted)
    .Inspect(ScreenCapability.Tap);
Assert(notRunningDecision.Reason == ScreenRefusal.NotRunning, "a stopped Shizuku service is distinguished from an unauthorized one");

var noGrantDecision = Gate(new FakeBridge { Result = authorized }, null, new[] { ScreenCapability.Swipe })
    .Inspect(ScreenCapability.Tap);
Assert(noGrantDecision.Reason == ScreenRefusal.NotGranted, "a capability the user never granted is refused per capability");
Assert(Gate(new FakeBridge { Result = authorized }, null, new[] { ScreenCapability.Swipe })
    .Inspect(ScreenCapability.Swipe).Allowed, "the capability that was granted does pass");

var sensitiveDecision = Gate(new FakeBridge { Result = authorized }, null, allGranted)
    .Inspect(ScreenCapability.TypeText, "com.tencent.mm", "输入支付密码");
Assert(sensitiveDecision.Reason == ScreenRefusal.SensitiveInput, "sensitive wording is refused for text input");
Assert(Gate(new FakeBridge { Result = authorized }, null, allGranted)
    .Inspect(ScreenCapability.Tap, "com.tencent.mm", "支付").Allowed,
    "the sensitive-word rule applies to typed text, not to unrelated capabilities");

var disabledDecision = Gate(new FakeBridge { Result = authorized }, null, allGranted, enabled: false)
    .Inspect(ScreenCapability.Tap);
Assert(disabledDecision.Reason == ScreenRefusal.Disabled, "the master switch is checked first");

var nullBridgeDecision = new ScreenGate(null, null, null, null, null).Inspect(ScreenCapability.Tap);
Assert(nullBridgeDecision.Reason == ScreenRefusal.BridgeUnbound, "a null bridge fails closed instead of throwing");

var throwingGrants = new ScreenGate(new FakeBridge { Result = authorized }, null,
    () => throw new InvalidOperationException("config unreadable"), null, null);
Assert(throwingGrants.Inspect(ScreenCapability.Tap).Reason == ScreenRefusal.NotGranted,
    "a broken grant source denies instead of crashing");

var throwingBridge = new ScreenGate(new ThrowingProbeBridge(), null, null, null, null);
Assert(throwingBridge.Status().Availability == ShizukuAvailability.BridgeUnbound,
    "a probe that throws is reported as unavailable, not as an exception");

// --------------------------------------------------------------- execution

var audit = new List<string>();

var missingApproval = Gate(new FakeBridge { Result = authorized }, null, allGranted, audit: audit.Add);
var missingDecision = await missingApproval.RunAsync(new ScreenAction(ScreenCapability.Tap, 10, 20));
Assert(missingDecision.Reason == ScreenRefusal.ApprovalMissing, "no approval surface means no action");

var deniedApproval = new FakeApproval { Answer = false };
var deniedBridge = new FakeBridge { Result = authorized };
var deniedGate = Gate(deniedBridge, deniedApproval, allGranted, audit: audit.Add);
var deniedDecision = await deniedGate.RunAsync(new ScreenAction(ScreenCapability.Tap, 10, 20));
Assert(deniedDecision.Reason == ScreenRefusal.ApprovalDenied, "a denied approval stops the action");
Assert(deniedBridge.Injections == 0, "a denied approval never reaches the bridge");
Assert(deniedApproval.Asked == 1, "the approval was actually asked");
Assert(deniedGate.Refused == 1 && deniedGate.Executed == 0, "counters reflect the refusal");

var approve = new FakeApproval();
var okBridge = new FakeBridge { Result = authorized };
var okGate = Gate(okBridge, approve, allGranted, audit: audit.Add);
var action = new ScreenAction(ScreenCapability.Swipe, 1, 2, 30, 40);
var okDecision = await okGate.RunAsync(action, "com.tencent.mm");
Assert(okDecision.Allowed, "a granted, approved, authorized action runs");
Assert(okBridge.Injections == 1 && okBridge.LastAction == action, "the bridge received the same action once");
Assert(okGate.Executed == 1 && okGate.Attempts == 1, "execution counters are exact");

var failingBridge = new FakeBridge { Result = authorized, Execute = false };
var failingGate = Gate(failingBridge, new FakeApproval(), allGranted);
var failingDecision = await failingGate.RunAsync(new ScreenAction(ScreenCapability.Tap, 1, 1));
Assert(failingDecision.Reason == ScreenRefusal.ExecutionFailed, "a bridge that refuses is reported as a failure");
Assert(failingGate.Executed == 0, "a failed injection is not counted as executed");

var throwingInjectBridge = new FakeBridge { Result = authorized, ThrowOnInject = true };
var throwingInjectGate = Gate(throwingInjectBridge, new FakeApproval(), allGranted);
var throwingDecision = await throwingInjectGate.RunAsync(new ScreenAction(ScreenCapability.Tap, 1, 1));
Assert(throwingDecision.Reason == ScreenRefusal.ExecutionFailed, "an exception during injection becomes a refusal, not a crash");

var blockedApproval = new FakeApproval();
var blockedRunBridge = new FakeBridge { Result = authorized };
var blockedRunGate = Gate(blockedRunBridge, blockedApproval, allGranted);
var blockedRun = await blockedRunGate.RunAsync(new ScreenAction(ScreenCapability.Tap), "com.eg.android.AlipayGphone");
Assert(blockedRun.Reason == ScreenRefusal.TargetBlocked, "a blocked target is refused when running too");
Assert(blockedApproval.Asked == 0, "a blocked target never reaches the approval prompt");
Assert(blockedRunBridge.Injections == 0, "a blocked target never reaches the bridge when running");

var unavailableApproval = new FakeApproval();
var unavailableGate = Gate(new FakeBridge { Result = unbound }, unavailableApproval, allGranted);
await unavailableGate.RunAsync(new ScreenAction(ScreenCapability.Tap));
Assert(unavailableApproval.Asked == 0, "an unavailable capability never asks the user to approve it");

var quietBridge = new FakeBridge { Result = authorized };
var inspectGate = Gate(quietBridge, new FakeApproval(), allGranted);
inspectGate.Inspect(ScreenCapability.Tap);
Assert(inspectGate.Attempts == 0 && inspectGate.Refused == 0 && quietBridge.Injections == 0,
    "Inspect has no side effects");

var typedText = "我的秘密口令";
var description = new ScreenAction(ScreenCapability.TypeText, Text: typedText).Describe();
Assert(description == $"输入 {typedText.Length} 个字符", "the approval text shows a character count, never the typed text");
Assert(!description.Contains(typedText, StringComparison.Ordinal), "the typed text itself is absent from the approval text");

Assert(audit.Any(line => line.Contains(ScreenRefusal.ApprovalDenied, StringComparison.Ordinal)),
    "refusals are written to the audit trail with their reason code");
Assert(audit.Any(line => line.Contains("已执行", StringComparison.Ordinal)),
    "executions are written to the audit trail");

// --------------------------------------------------------------- frame data

Assert(Math.Abs(FrameMeter.Percentile(new[] { 1.0, 2, 3, 4, 5 }, 0.5) - 3) < 1e-9, "p50 of 1..5 is 3");
Assert(Math.Abs(FrameMeter.Percentile(new[] { 1.0, 2, 3, 4, 5 }, 0) - 1) < 1e-9, "p0 is the minimum");
Assert(Math.Abs(FrameMeter.Percentile(new[] { 1.0, 2, 3, 4, 5 }, 1) - 5) < 1e-9, "p100 is the maximum");
Assert(FrameMeter.Percentile(Array.Empty<double>(), 0.5) == 0, "an empty sample set reports zero");

var meter = new FrameMeter(capacity: 10);
Assert(!meter.Snapshot().HasSamples, "a fresh meter has no samples");

foreach (var value in new[] { 16.0, 16, 16, 16, 16, 16, 16, 16, 16, 40 }) meter.Record(value);
var report = meter.Snapshot();
Assert(report.Samples == 10, "every valid sample is kept");
Assert(Math.Abs(report.P50 - 16) < 1e-9, "median frame time is 16ms");
Assert(Math.Abs(report.Worst - 40) < 1e-9, "worst frame is reported");
Assert(Math.Abs(report.JankRatio - 0.1) < 1e-9, "one 40ms frame out of ten is a 10% jank ratio");

var thresholdMeter = new FrameMeter(capacity: 8);
thresholdMeter.Record(33.0);
Assert(Math.Abs(thresholdMeter.Snapshot().JankRatio) < 1e-9, "a frame near 2x target is not jank");
thresholdMeter.Reset();
thresholdMeter.Record(34.0);
Assert(Math.Abs(thresholdMeter.Snapshot().JankRatio - 1.0) < 1e-9, "a frame beyond 2x target is jank");

var noiseMeter = new FrameMeter(capacity: 20);
noiseMeter.Record(0);
noiseMeter.Record(-5);
noiseMeter.Record(double.NaN);
noiseMeter.Record(double.PositiveInfinity);
noiseMeter.Record(50_000);
Assert(!noiseMeter.Snapshot().HasSamples, "nonsense frame times are dropped, not clamped");

var ringMeter = new FrameMeter(capacity: 3);
foreach (var value in new[] { 10.0, 20, 30, 40 }) ringMeter.Record(value);
var ringReport = ringMeter.Snapshot();
Assert(ringReport.Samples == 3 && Math.Abs(ringReport.Worst - 40) < 1e-9 && Math.Abs(ringReport.P50 - 30) < 1e-9,
    "the ring buffer keeps the most recent window");

// ------------------------------------------------------------ the sampler

var unsupported = new PerformanceMonitor();
var (unsupportedOk, unsupportedMessage) = await unsupported.SampleAsync(TimeSpan.FromMilliseconds(10));
Assert(!unsupportedOk && unsupportedMessage.Contains("没有", StringComparison.Ordinal),
    "a platform without a frame channel says so instead of reporting a number");

var fakeTicker = new FakeTicker();
var monitor = new PerformanceMonitor(fakeTicker);
var (ok, message) = await monitor.SampleAsync(TimeSpan.FromMilliseconds(250));
Assert(ok, "a supported platform produces a report");
Assert(monitor.LastReport.HasSamples, "the last report keeps its samples");
Assert(monitor.LastReport.Samples >= 5, "the sampler recorded the frames the ticker fed it");
Assert(message.Contains("p95", StringComparison.Ordinal), "the summary reports p95");
Assert(!monitor.IsSampling && !fakeTicker.Running, "the ticker is stopped when the window ends");
Assert(monitor.DescribeMemory().Contains("托管堆", StringComparison.Ordinal), "memory is reported alongside frame time");

var concurrentTicker = new FakeTicker();
var concurrent = new PerformanceMonitor(concurrentTicker);
var first = concurrent.SampleAsync(TimeSpan.FromMilliseconds(200));
var (secondOk, secondMessage) = await concurrent.SampleAsync(TimeSpan.FromMilliseconds(10));
Assert(!secondOk && secondMessage.Contains("已经在测", StringComparison.Ordinal),
    "a second measurement is refused while one is running");
await first;

Console.WriteLine();
Console.WriteLine($"All {checks} checks passed.");
return 0;

sealed class FakeBridge : IShizukuBridge
{
    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public ShizukuProbeResult Result { get; init; } = new(ShizukuAvailability.Authorized, "1.0", "fake");

    public bool Execute { get; init; } = true;

    public bool ThrowOnInject { get; init; }

    public int Injections { get; private set; }

    public ScreenAction? LastAction { get; private set; }

    public ShizukuProbeResult Probe() => Result;

    public bool OpenShizukuApp(out string message)
    {
        message = "fake";
        return true;
    }

    public Task<ScreenExecutionResult> InjectAsync(ScreenAction action, CancellationToken cancellationToken = default)
    {
        Injections++;
        LastAction = action;

        if (ThrowOnInject) throw new InvalidOperationException("injection blew up");

        return Task.FromResult(Execute
            ? ScreenExecutionResult.Ok()
            : ScreenExecutionResult.Failed("device said no"));
    }
}

sealed class ThrowingProbeBridge : IShizukuBridge
{
    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public ShizukuProbeResult Probe() => throw new InvalidOperationException("probe blew up");

    public bool OpenShizukuApp(out string message)
    {
        message = "fake";
        return false;
    }

    public Task<ScreenExecutionResult> InjectAsync(ScreenAction action, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScreenExecutionResult.Failed("no"));
}

sealed class FakeApproval : IScreenApproval
{
    public bool Answer { get; init; } = true;

    public int Asked { get; private set; }

    public Task<bool> AskAsync(string capabilityLabel, string description, CancellationToken cancellationToken = default)
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}

/// <summary>Feeds a steady 16ms cadence, like a 60Hz display would.</summary>
sealed class FakeTicker : IFrameTicker
{
    private CancellationTokenSource? _stop;

    public bool IsSupported => true;

    public bool Running { get; private set; }

    public bool Start(Action<double> onFrame)
    {
        _stop = new CancellationTokenSource();
        Running = true;
        var token = _stop.Token;

        _ = Task.Run(async () =>
        {
            var frame = 0;
            while (!token.IsCancellationRequested && frame < 500)
            {
                onFrame(frame % 20 == 0 ? 40 : 16);
                frame++;
                try
                {
                    await Task.Delay(4, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        return true;
    }

    public void Stop()
    {
        Running = false;
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }
}
