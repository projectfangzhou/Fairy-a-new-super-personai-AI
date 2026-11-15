using System.IO;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Notifications;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var workspace = Path.Combine(AppContext.BaseDirectory, "notif-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    var clock = DateTimeOffset.UtcNow;
    DateTimeOffset Clock() => clock;

    // ---- 1. Preferences decide what is wanted and what counts as quiet ----
    {
        var defaults = NotificationPreferences.Defaults;
        Assert(defaults.Allows(NotificationKind.ApprovalPending), "approvals are wanted by default");
        Assert(defaults.Allows(NotificationKind.TaskCompleted), "completions are wanted by default");

        var off = defaults with { TaskCompleted = false, Enabled = false };
        Assert(!off.Allows(NotificationKind.TaskCompleted), "a kind can be turned off");
        Assert(!off.Allows(NotificationKind.ApprovalPending), "the master switch turns everything off");

        var quiet = defaults with { QuietHoursEnabled = true, QuietHoursStart = 23, QuietHoursEnd = 7 };
        var night = new DateTimeOffset(2026, 11, 8, 1, 30, 0, TimeSpan.FromHours(8));
        var morning = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(8));
        var lateEvening = new DateTimeOffset(2026, 11, 8, 23, 30, 0, TimeSpan.FromHours(8));

        Assert(quiet.IsQuiet(night), "a wrapped window covers the middle of the night");
        Assert(quiet.IsQuiet(lateEvening), "a wrapped window starts before midnight");
        Assert(!quiet.IsQuiet(morning), "the window ends in the morning");

        Assert(!quiet.ShouldPresent(NotificationKind.TaskCompleted, night),
            "a finished task does not interrupt quiet hours");
        Assert(quiet.ShouldPresent(NotificationKind.ApprovalPending, night),
            "an approval still interrupts: silence would block the work");
    }

    // ---- 2. The centre records, dedupes and keeps unread state ----
    {
        var settings = new FakeSettings();
        var notifications = new NotificationCenter(settings, Path.Combine(workspace, "first"), Clock);

        Assert(notifications.UnreadCount == 0, "it starts empty");

        var added = notifications.Add(NotificationKind.TaskCompleted, "任务完成", "整理日志", "t1", "t1",
            NotificationSeverity.Success);
        Assert(notifications.UnreadCount == 1, "recording makes it unread");
        Assert(notifications.Recent()[0].Body == "整理日志", "the body is kept as given");
        Assert(notifications.Recent()[0].IsUnread, "a new notification is unread");

        // The same event again within the window collapses into the same entry.
        clock = clock.AddMinutes(1);
        var repeated = notifications.Add(NotificationKind.TaskCompleted, "任务完成", "整理日志（重试）", "t1", "t1",
            NotificationSeverity.Success);
        Assert(repeated.Id == added.Id, "a repeat keeps the original entry");
        Assert(notifications.Recent().Count == 1, "a repeat does not add a line");
        Assert(notifications.Recent()[0].Body.Contains("重试", StringComparison.Ordinal), "the repeat refreshes the text");

        // Past the window it is a new entry again.
        clock = clock.AddMinutes(30);
        notifications.Add(NotificationKind.TaskCompleted, "任务完成", "整理日志", "t1", "t1", NotificationSeverity.Success);
        Assert(notifications.Recent().Count == 2, "after the window the same event is a new entry");

        var id = notifications.Recent()[0].Id;
        Assert(notifications.MarkRead(id), "an entry can be marked read");
        Assert(!notifications.Recent().First(entry => entry.Id == id).IsUnread, "marking it read sticks");
        Assert(notifications.UnreadCount == 1, "one of the two is still unread");

        Assert(notifications.MarkAllRead() == 1, "mark-all-read reports what it changed");
        Assert(notifications.UnreadCount == 0, "nothing is unread afterwards");
        Assert(!notifications.MarkRead("n-that-does-not-exist"), "an unknown id is refused, not crashed on");
    }

    // ---- 3. A kind the user turned off is not recorded at all ----
    {
        var settings = new FakeSettings
        {
            Current = NotificationPreferences.Defaults with { TaskCompleted = false }
        };
        var notifications = new NotificationCenter(settings, Path.Combine(workspace, "muted"), Clock);

        notifications.Add(NotificationKind.TaskCompleted, "任务完成", "不该出现", "t1", "t1", NotificationSeverity.Success);
        Assert(notifications.Recent().Count == 0, "a disabled kind leaves no history");

        notifications.Add(NotificationKind.ApprovalPending, "需要确认", "该出现", "t1", "i1", NotificationSeverity.Warning);
        Assert(notifications.Recent().Count == 1, "an enabled kind is still recorded");
    }

    // ---- 4. History survives a restart, and a broken file does not lose the app ----
    {
        var directory = Path.Combine(workspace, "persist");
        var settings = new FakeSettings();

        var first = new NotificationCenter(settings, directory, Clock);
        {
            first.Add(NotificationKind.TaskFailed, "任务失败", "缺依赖", "t9", "t9", NotificationSeverity.Error);
            first.MarkAllRead();
            first.Add(NotificationKind.ApprovalPending, "需要确认", "覆盖文件", "t9", "i9", NotificationSeverity.Warning);
        }

        var reopened = new NotificationCenter(settings, directory, Clock);
        {
            var recent = reopened.Recent();
            Assert(recent.Count == 2, "both entries come back");
            Assert(reopened.UnreadCount == 1, "read state comes back too");
        }

        var broken = Path.Combine(workspace, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "history.json"), "{ not json");

        var recovered = new NotificationCenter(settings, broken, Clock);
        {
            Assert(recovered.Recent().Count == 0, "an unreadable history starts empty instead of throwing");
            Assert(Directory.GetFiles(broken, "history.json.unreadable-*").Length == 1,
                "the unreadable file is kept for inspection");
        }
    }

    // ---- 5. The bridge turns agent events into notifications, once each ----
    {
        var settings = new FakeSettings();
        var notifications = new NotificationCenter(settings, Path.Combine(workspace, "bridge"), Clock);
        var center = new AgentControlCenter();
        using var bridge = new NotificationBridge(center, notifications);

        var task = center.Enqueue("整理日志");
        task.Start();

        var approval = task.AskApprovalAsync("写入文件 D:\\Dev\\a.txt", RiskCategory.DeleteOrOverwriteData);
        PumpUntil(() => center.Pending().Count == 1, "the approval is open");

        var pending = notifications.Recent().FirstOrDefault();
        Assert(pending is not null, "an open approval becomes a notification");
        Assert(pending!.Kind == NotificationKind.ApprovalPending, "it is filed as an approval");
        Assert(pending.Severity == NotificationSeverity.Warning, "an approval is a warning, not information");
        Assert(pending.TargetId is not null, "it points at the interaction so a click can open it");
        Assert(pending.Actions.Count == 2, "it offers allow and deny");

        // A second event for the same interaction must not stack up.
        center.RequestPause(task.Id);
        PumpUntil(() => notifications.Recent().Count(entry => entry.Kind == NotificationKind.ApprovalPending) == 1,
            "still exactly one approval notification");

        center.Answer(pending.TargetId!, true);
        Assert(PumpUntil(() => approval.IsCompleted, "answering settles the task"), "answering settles the task");
        Assert(notifications.Recent().All(entry => entry.Kind != NotificationKind.ApprovalPending || !entry.IsUnread),
            "an answered approval stops being unread");

        center.Answer("does-not-exist", true);

        // A finished task notifies once, with its title in the body.
        var finished = center.Enqueue("写报告");
        finished.Start();
        finished.Complete();
        var done = notifications.Recent().FirstOrDefault(entry => entry.Kind == NotificationKind.TaskCompleted);
        Assert(done is not null, "a completed task notifies");
        Assert(done!.Body.Contains("写报告", StringComparison.Ordinal), "the body names the task");

        var failed = center.Enqueue("跑构建");
        failed.Start();
        failed.Fail("编译失败");
        var failure = notifications.Recent().FirstOrDefault(entry => entry.Kind == NotificationKind.TaskFailed);
        Assert(failure is not null && failure.Severity == NotificationSeverity.Error, "a failure is an error notification");
        Assert(failure!.Body.Contains("编译失败", StringComparison.Ordinal), "the failure reason is carried");
    }

    // ---- 6. Quiet hours: recorded but not presented ----
    {
        var settings = new FakeSettings
        {
            Current = NotificationPreferences.Defaults with
            {
                QuietHoursEnabled = true, QuietHoursStart = 0, QuietHoursEnd = 23
            }
        };

        var notifications = new NotificationCenter(settings, Path.Combine(workspace, "quiet"), Clock);
        var presented = new List<NotificationKind>();
        var recorded = new List<NotificationKind>();
        notifications.Presented += entry => presented.Add(entry.Kind);
        notifications.Recorded += entry => recorded.Add(entry.Kind);

        notifications.Add(NotificationKind.TaskCompleted, "任务完成", "夜间完成", "t1", "t1", NotificationSeverity.Success);
        Assert(recorded.Count == 1, "it is still recorded during quiet hours");
        Assert(presented.Count == 0, "but nothing interrupts");
        Assert(notifications.UnreadCount == 1, "and it waits as unread");

        notifications.Add(NotificationKind.ApprovalPending, "需要确认", "夜间审批", "t2", "i2", NotificationSeverity.Warning);
        Assert(presented.Count == 1 && presented[0] == NotificationKind.ApprovalPending,
            "an approval is presented even in quiet hours");
    }

    Console.WriteLine($"All {checks} notification checks passed.");
    return 0;
}
finally
{
    try { Directory.Delete(workspace, true); } catch (IOException) { }
}

static bool PumpUntil(Func<bool> condition, string name, int timeoutMs = 2000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition()) return true;
        Thread.Sleep(10);
    }

    Console.Error.WriteLine("TIMEOUT: " + name);
    return condition();
}

internal sealed class FakeSettings : INotificationSettings
{
    public NotificationPreferences Current { get; set; } = NotificationPreferences.Defaults;

    public NotificationPreferences Read() => Current;

    public (bool Ok, string Message) Apply(NotificationPreferences preferences)
    {
        Current = preferences;
        return (true, "已保存");
    }
}
