using MyAiAssistant.Services.Notifications;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-systemnotify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ------------------------------------------------------------ which kinds may leave

    var defaults = NotificationPreferences.Defaults;
    Assert(SystemNotificationPolicy.IsSystemEligible(NotificationKind.System, defaults),
        "ordinary notifications are eligible by default");
    Assert(!SystemNotificationPolicy.IsSystemEligible(NotificationKind.TaskCompleted, defaults),
        "task events stay in the app unless mirrored");
    Assert(!SystemNotificationPolicy.IsSystemEligible(NotificationKind.TransferIncoming, defaults),
        "transfer events stay in the app unless mirrored");

    var mirrored = defaults with { MirrorKeyEvents = true };
    Assert(SystemNotificationPolicy.IsSystemEligible(NotificationKind.TaskCompleted, mirrored),
        "mirroring sends task completion to the OS");
    Assert(SystemNotificationPolicy.IsSystemEligible(NotificationKind.TaskFailed, mirrored),
        "mirroring sends task failure to the OS");

    // A toast cannot carry the answer, and a stale one in the OS history outlives the question.
    Assert(!SystemNotificationPolicy.IsSystemEligible(NotificationKind.ApprovalPending, mirrored),
        "approvals never go to the OS, even with mirroring on");
    Assert(!SystemNotificationPolicy.IsSystemEligible(NotificationKind.QuestionPending, mirrored),
        "questions never go to the OS either");

    var systemOff = defaults with { SystemEnabled = false };
    Assert(!SystemNotificationPolicy.IsSystemEligible(NotificationKind.System, systemOff),
        "turning system notifications off disables the ordinary kind too");

    // ------------------------------------------------------------ what the text may carry

    Assert(SystemNotificationPolicy.Scrub(@"任务失败：写入 D:\Dev\客户报价\2026.xlsx 失败", 200)
               .Contains("（路径已省略）", StringComparison.Ordinal) &&
           !SystemNotificationPolicy.Scrub(@"D:\Dev\客户报价\2026.xlsx", 200).Contains("客户报价", StringComparison.Ordinal),
        "windows paths are removed");

    Assert(!SystemNotificationPolicy.Scrub(@"\\fileserver\hr\薪资表.xlsx", 200).Contains("薪资表", StringComparison.Ordinal),
        "unc paths are removed");
    Assert(!SystemNotificationPolicy.Scrub("读取 /home/user/.ssh/id_rsa 失败", 200).Contains("id_rsa", StringComparison.Ordinal),
        "posix paths are removed");
    Assert(!SystemNotificationPolicy.Scrub("token sk-abcdef0123456789abcdef0123456789 rejected", 200)
            .Contains("abcdef0123456789", StringComparison.Ordinal),
        "long tokens are removed");
    Assert(SystemNotificationPolicy.Scrub("第一行\n第二行", 200) == "第一行 第二行", "newlines collapse to one line");
    Assert(SystemNotificationPolicy.Scrub(new string('长', 300), 60).Length == 61, "long text is truncated with an ellipsis");

    {
        var (title, body) = SystemNotificationPolicy.Compose(
            NotificationKind.TaskFailed, "任务失败", "汇总 D:\\Dev\\客户报价.xlsx 时出错", includeDetails: false);
        Assert(title == "任务失败", "title survives");
        Assert(body == "有任务失败，打开仙灵查看原因", "details off replaces task text with a fixed sentence");

        var (_, detailed) = SystemNotificationPolicy.Compose(
            NotificationKind.TaskFailed, "任务失败", "汇总 D:\\Dev\\客户报价.xlsx 时出错", includeDetails: true);
        Assert(detailed.Contains("（路径已省略）", StringComparison.Ordinal) &&
               !detailed.Contains("客户报价", StringComparison.Ordinal),
            "details on still strips paths");

        var (_, systemBody) = SystemNotificationPolicy.Compose(
            NotificationKind.System, "仙灵有新版本", "发现版本 3.0.0-alpha.2", includeDetails: false);
        Assert(systemBody.Contains("3.0.0-alpha.2", StringComparison.Ordinal),
            "our own status text is shown even with details off");
    }

    // ------------------------------------------------------------ relay behaviour

    NotificationPreferences prefs = NotificationPreferences.Defaults;
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    var settings = new MutableSettings(() => prefs);
    var center = new NotificationCenter(settings, Path.Combine(directory, "relay"));
    var channel = new RecordingChannel();
    using var relay = new SystemNotificationRelay(center, settings, channel, () => now);

    center.Add(NotificationKind.System, "仙灵有新版本", "发现版本 3.0.0-alpha.2",
        severity: NotificationSeverity.Info, dedupeKey: "update:1");
    Assert(relay.Delivered == 1 && channel.Sent.Count == 1, "an ordinary notification reaches the OS channel");
    Assert(channel.Sent[0].Title == "仙灵有新版本", "the title is passed through");
    Assert(channel.Sent[0].Body.Contains("3.0.0-alpha.2", StringComparison.Ordinal), "the body is passed through");

    center.Add(NotificationKind.TaskCompleted, "任务完成", "已经做完",
        taskId: "t1", targetId: "t1", severity: NotificationSeverity.Success, dedupeKey: "task:t1:done");
    Assert(relay.Delivered == 1 && relay.LastSkipReason == "这类通知只在仙灵内提醒",
        "task completion stays in the app by default");

    prefs = prefs with { MirrorKeyEvents = true };
    center.Add(NotificationKind.TaskCompleted, "任务完成", "已经做完",
        taskId: "t2", targetId: "t2", severity: NotificationSeverity.Success, dedupeKey: "task:t2:done");
    Assert(relay.Delivered == 2 && channel.Sent[1].Body == "任务已完成，打开仙灵查看结果",
        "with mirroring on the OS gets a generic sentence, not the task text");

    prefs = prefs with { SystemDetails = true };
    center.Add(NotificationKind.TaskFailed, "任务失败", "写入 D:\\Dev\\x.txt 失败",
        taskId: "t3", targetId: "t3", severity: NotificationSeverity.Error, dedupeKey: "task:t3:failed");
    Assert(relay.Delivered == 3 && !channel.Sent[2].Body.Contains("x.txt", StringComparison.Ordinal),
        "details on still never leaks a path");

    center.Add(NotificationKind.ApprovalPending, "需要你的确认", "删除 12 个文件？",
        taskId: "t4", targetId: "i4", severity: NotificationSeverity.Warning, dedupeKey: "interaction:i4");
    Assert(relay.Delivered == 3, "approvals never reach the OS channel");

    // Same event twice: one delivery, because the history collapses it too.
    center.Add(NotificationKind.System, "仙灵有新版本", "发现版本 3.0.0-alpha.2",
        severity: NotificationSeverity.Info, dedupeKey: "update:1");
    Assert(relay.Delivered == 3 && relay.LastSkipReason == "同一事项刚刚提醒过",
        "a repeat of the same event is not delivered twice");

    now = now.AddMinutes(11);
    center.Add(NotificationKind.System, "仙灵有更新的版本", "发现版本 3.0.0-alpha.3",
        severity: NotificationSeverity.Info, dedupeKey: "update:2");
    Assert(relay.Delivered == 4, "a different event after the window is delivered");

    // Quiet hours silence the OS as well, not only the HUD. Built in local time on purpose:
    // the preference compares local hours, so a UTC literal would make this test timezone-dependent.
    prefs = prefs with { QuietHoursEnabled = true, QuietHoursStart = 23, QuietHoursEnd = 7 };
    var quietLocal = DateTime.Today.AddHours(1.5);
    now = new DateTimeOffset(quietLocal, TimeZoneInfo.Local.GetUtcOffset(quietLocal));
    center.Add(NotificationKind.System, "夜里的事", "免打扰", severity: NotificationSeverity.Info, dedupeKey: "night");
    Assert(relay.Delivered == 4 && relay.LastSkipReason == "免打扰时段", "quiet hours stop OS delivery");

    // A kind the user turned off never even reaches the history, so it cannot reach the OS.
    prefs = NotificationPreferences.Defaults with { SystemEnabled = true, TaskCompleted = false };
    center.Add(NotificationKind.TaskCompleted, "任务完成", "关掉的类型",
        taskId: "t9", targetId: "t9", severity: NotificationSeverity.Success, dedupeKey: "task:t9:done");
    Assert(relay.Delivered == 4, "a disabled kind is not delivered");

    prefs = NotificationPreferences.Defaults with { SystemEnabled = false };
    center.Add(NotificationKind.System, "又一条", "系统通知关闭", severity: NotificationSeverity.Info, dedupeKey: "off");
    Assert(relay.Delivered == 4 && relay.LastSkipReason == "系统通知已关闭", "the master switch applies");

    // ------------------------------------------------------------ failures stay contained

    {
        var failSettings = new MutableSettings(() => NotificationPreferences.Defaults);
        var failCenter = new NotificationCenter(failSettings, Path.Combine(directory, "fail"));
        var unavailable = new UnavailableSystemNotificationChannel("本平台没有通知通道");
        using var failRelay = new SystemNotificationRelay(failCenter, failSettings, unavailable);

        failCenter.Add(NotificationKind.System, "试一下", "没有通道", severity: NotificationSeverity.Info, dedupeKey: "u1");
        Assert(failRelay.Delivered == 0 && failRelay.Suppressed == 1,
            "an unavailable channel suppresses instead of throwing");
        Assert(failRelay.LastSkipReason!.Contains("通道不可用", StringComparison.Ordinal),
            "the reason names the channel");
    }

    {
        var throwSettings = new MutableSettings(() => NotificationPreferences.Defaults);
        var throwCenter = new NotificationCenter(throwSettings, Path.Combine(directory, "throw"));
        var throwing = new DelegateSystemNotificationChannel((_, _, _) => throw new InvalidOperationException("boom"));
        using var throwRelay = new SystemNotificationRelay(throwCenter, throwSettings, throwing);

        throwCenter.Add(NotificationKind.System, "试一下", "抛异常", severity: NotificationSeverity.Info, dedupeKey: "x1");
        Assert(throwRelay.Delivered == 0, "a throwing channel does not deliver");
        Assert(throwRelay.LastSkipReason is not null, "and it is recorded");
        Assert(!throwing.IsAvailable, "the channel marks itself unavailable after failing");
    }

    // ------------------------------------------------------------ libnotify arguments

    {
        var calls = new List<(string Executable, IReadOnlyList<string> Arguments)>();
        var libnotify = new LibnotifySystemNotificationChannel((exe, args) => calls.Add((exe, args)));

        Assert(libnotify.Show("标题", "正文", NotificationSeverity.Error), "libnotify reports success");
        Assert(calls.Count == 1 && calls[0].Executable == "notify-send", "notify-send is the executable");

        var libArgs = calls[0].Arguments;
        Assert(libArgs.Contains("--urgency=critical"), "error maps to critical urgency");
        var separator = libArgs.ToList().IndexOf("--");
        Assert(separator >= 0 && separator < libArgs.Count - 2,
            "a -- separator keeps a leading-dash title from becoming an option");
        Assert(libArgs[^2] == "标题" && libArgs[^1] == "正文", "title and body are the last two arguments");
        Assert(libArgs[0].StartsWith("--app-name=", StringComparison.Ordinal), "the app name is set");

        Assert(new LibnotifySystemNotificationChannel((_, _) => { }).BuildArguments("t", "b", NotificationSeverity.Info)
            .Contains("--urgency=low"), "info maps to low urgency");
        Assert(new LibnotifySystemNotificationChannel((_, _) => { }).BuildArguments("t", "b", NotificationSeverity.Warning)
            .Contains("--urgency=normal"), "warning maps to normal urgency");

        var broken = new LibnotifySystemNotificationChannel((_, _) => throw new InvalidOperationException("no notify-send"));
        Assert(!broken.Show("t", "b", NotificationSeverity.Info), "a missing notify-send is a failure");
        Assert(!broken.IsAvailable && broken.LastError is not null, "and it marks itself unavailable");
    }

    Console.WriteLine($"All {checks} system notification checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}

internal sealed class MutableSettings : INotificationSettings
{
    private readonly Func<NotificationPreferences> _read;

    public MutableSettings(Func<NotificationPreferences> read) => _read = read;

    public NotificationPreferences Read() => _read();

    public (bool Ok, string Message) Apply(NotificationPreferences preferences) => (true, "ok");
}

internal sealed class RecordingChannel : ISystemNotificationChannel
{
    public List<(string Title, string Body, NotificationSeverity Severity)> Sent { get; } = new();

    public string Platform => "test";

    public bool IsAvailable { get; set; } = true;

    public string? LastError { get; private set; }

    public bool Show(string title, string body, NotificationSeverity severity)
    {
        if (!IsAvailable)
        {
            LastError = "通道被标记为不可用";
            return false;
        }

        Sent.Add((title, body, severity));
        return true;
    }
}
