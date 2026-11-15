using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

async Task WaitUntil(Func<bool> condition, string name, int timeoutMs = 2000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition()) { checks++; return; }
        await Task.Delay(10);
    }
    throw new Exception("TIMEOUT: " + name);
}

var center = new AgentControlCenter();

// 1. Enqueue then start.
var task = center.Enqueue("整理下载目录", "Pi", "mimo");
Assert(center.Task(task.Id)!.State == AgentTaskState.Queued, "task starts queued");
Assert(task.Start(), "queued task can start");
Assert(task.State == AgentTaskState.Running, "task running after start");
Assert(!task.Start(), "start is not repeatable");

// 2. Progress is visible to every reader.
task.ReportProgress(2, 5, "扫描文件");
var snapshot = center.Task(task.Id)!;
Assert(snapshot.Step == 2 && snapshot.TotalSteps == 5 && snapshot.StepLabel == "扫描文件", "progress recorded");

// 3. Pause only sets intent; the task keeps running until it reaches a boundary.
Assert(center.RequestPause(task.Id), "pause accepted while running");
var afterPauseRequest = center.Task(task.Id)!;
Assert(afterPauseRequest.State == AgentTaskState.Running && afterPauseRequest.PauseRequested,
    "pause is deferred, not immediate");
Assert(!center.RequestPause(task.Id), "duplicate pause request rejected");

var waiting = task.WaitIfPausedAsync();
await WaitUntil(() => task.State == AgentTaskState.Paused, "task reaches paused at boundary");
Assert(!waiting.IsCompleted, "task stays blocked while paused");
Assert(!center.RequestPause(task.Id), "cannot pause an already paused task");

// 4. Resume releases the boundary.
Assert(center.RequestResume(task.Id), "resume accepted while paused");
await waiting;
Assert(task.State == AgentTaskState.Running, "task running after resume");
Assert(!center.RequestResume(task.Id), "resume is not repeatable");

// 5. Stop is immediate, terminal and cannot be undone.
Assert(center.RequestStop(task.Id), "stop accepted");
Assert(task.State == AgentTaskState.Cancelled, "stop is immediate");
var threw = false;
try { task.ThrowIfStopped(); } catch (OperationCanceledException) { threw = true; }
Assert(threw, "stopped task refuses further work");
Assert(!task.Complete(), "cannot complete a stopped task");
Assert(!center.RequestResume(task.Id), "cannot resume a stopped task");
Assert(!center.RequestStop(task.Id), "stop is not repeatable");

// 6. Stopping while paused releases the waiter instead of hanging it.
var paused = center.Enqueue("暂停后停止");
paused.Start();
center.RequestPause(paused.Id);
var blocked = paused.WaitIfPausedAsync();
await WaitUntil(() => paused.State == AgentTaskState.Paused, "second task paused");
center.RequestStop(paused.Id);
var cancelledWhilePaused = false;
try { await blocked; } catch (OperationCanceledException) { cancelledWhilePaused = true; }
Assert(cancelledWhilePaused, "stop releases a paused waiter with cancellation");

// 7. Completion and failure are terminal and keep their reason.
var done = center.Enqueue("正常完成");
done.Start();
done.ReportProgress(3, 3, "完成");
Assert(done.Complete(), "complete accepted");
Assert(done.State == AgentTaskState.Completed, "state completed");
Assert(!done.Complete() && !done.Fail("x"), "completion is terminal");

var failed = center.Enqueue("失败任务");
failed.Start();
Assert(failed.Fail("磁盘已满"), "fail accepted");
Assert(center.Task(failed.Id)!.Error == "磁盘已满", "failure reason recorded");

// 8. Approval blocks until the user answers, and the pending item is visible meanwhile.
var approvalTask = center.Enqueue("删除临时文件");
approvalTask.Start();
var approval = approvalTask.AskApprovalAsync("删除 12 个临时文件？", RiskCategory.DeleteOrOverwriteData);
await WaitUntil(() => center.Pending().Count == 1, "approval shows up as pending");
var pending = center.Pending()[0];
Assert(pending.Kind == PendingKind.Approval && pending.Risk == RiskCategory.DeleteOrOverwriteData,
    "pending carries kind and risk");
Assert(!approval.IsCompleted, "approval blocks the task");
Assert(center.Answer(pending.Id, true), "approval answered");
Assert(await approval, "task observes approval");
Assert(center.Pending().Count == 0, "answered interaction is cleared");
Assert(!center.Answer(pending.Id, true), "cannot answer twice");

// 9. Rejection is reported as false, not as an exception.
var rejected = approvalTask.AskApprovalAsync("再删除一次？", RiskCategory.DeleteOrOverwriteData);
await WaitUntil(() => center.Pending().Count == 1, "second approval pending");
center.Answer(center.Pending()[0].Id, false);
Assert(!await rejected, "rejection returns false");

// 10. Questions carry text back.
var question = approvalTask.AskQuestionAsync("目标目录是哪个？");
await WaitUntil(() => center.Pending().Count == 1, "question pending");
var questionId = center.Pending()[0].Id;
Assert(center.Pending()[0].Kind == PendingKind.Question, "pending kind question");
center.AnswerText(questionId, "D:\\work");
Assert(await question == "D:\\work", "answer text delivered");

// 11. Cancelling an approval withdraws it instead of leaving it orphaned.
using (var cancellation = new CancellationTokenSource())
{
    var pendingApproval = approvalTask.AskApprovalAsync("等他回话", RiskCategory.ExternalTransfer, cancellation.Token);
    await WaitUntil(() => center.Pending().Count == 1, "third approval pending");
    await cancellation.CancelAsync();
    var cancelled = false;
    try { await pendingApproval; } catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled, "cancellation surfaces as OperationCanceledException");
    await WaitUntil(() => center.Pending().Count == 0, "cancelled interaction withdrawn");
}

// 12. A stopped task cannot open new interactions.
center.RequestStop(approvalTask.Id);
var stoppedApproval = false;
try { await approvalTask.AskApprovalAsync("停止后还要审吗", RiskCategory.DeleteOrOverwriteData); }
catch (OperationCanceledException) { stoppedApproval = true; }
Assert(stoppedApproval, "stopped task cannot ask for approval");
Assert(center.Pending().Count == 0, "no interactions left behind");

// 13. One shared event stream carries the whole story.
var types = center.Events(500).Select(e => e.Type).ToHashSet();
foreach (var expected in new[]
         {
             AgentControlEventType.TaskEnqueued, AgentControlEventType.TaskStarted,
             AgentControlEventType.TaskProgress, AgentControlEventType.TaskPauseRequested,
             AgentControlEventType.TaskPaused, AgentControlEventType.TaskResumed,
             AgentControlEventType.TaskStopRequested, AgentControlEventType.TaskCompleted,
             AgentControlEventType.TaskFailed, AgentControlEventType.TaskCancelled,
             AgentControlEventType.InteractionOpened, AgentControlEventType.InteractionAnswered,
             AgentControlEventType.InteractionWithdrawn
         })
{
    Assert(types.Contains(expected), $"event {expected} emitted");
}

// 14. Subscribers receive events; a throwing subscriber cannot break the center.
var received = 0;
center.Changed += _ => received++;
center.Changed += _ => throw new InvalidOperationException("listener boom");
var listenerTask = center.Enqueue("订阅测试");
listenerTask.Start();
Assert(received >= 2, "subscribers notified and failures isolated");

// 15. Snapshot capability flags match the state machine.
var beforeCaps = center.Tasks().Count;
var caps = center.Enqueue("能力位");
caps.Start();
var running = caps.Snapshot();
Assert(running.CanPause && !running.CanResume && running.CanStop && !running.IsTerminal, "running capabilities");
center.RequestStop(caps.Id);
var stopped = caps.Snapshot();
Assert(stopped.IsTerminal && !stopped.CanPause && !stopped.CanStop, "stopped capabilities");
Assert(center.Tasks().Count == beforeCaps + 1, "every enqueued task is listed");

// 16. Resource sampling returns locally measured numbers.
var usage = ResourceUsage.Sample();
Assert(usage.WorkingSetBytes > 0 && usage.ThreadCount > 0, "resource usage sampled");

// 17. The approval gate surfaces through the task state, and answering there releases it.
var auditDir = Path.Combine(Path.GetTempPath(), "fairyai-agent-bridge-" + Guid.NewGuid().ToString("N"));
try
{
    var audit = new OperationAudit(auditDir);
    var gate = new ApprovalGate(new PermissionPolicy(), audit);
    var gated = center.Enqueue("受控删除");
    gated.Start();
    gate.Approver = TaskApproval.For(gated);

    var authorization = gate.AuthorizeAsync(
        new ActionRequest("file.delete", RiskCategory.DeleteOrOverwriteData, "删除缓存", @"C:\work\cache"));
    await WaitUntil(() => center.Pending().Count == 1, "gate request reaches the task state");
    var surfaced = center.Pending()[0];
    Assert(surfaced.Kind == PendingKind.Approval && surfaced.Risk == RiskCategory.DeleteOrOverwriteData,
        "gate approval carries its risk category");
    Assert(surfaced.Prompt.Contains("C:\\work\\cache", StringComparison.Ordinal), "prompt shows the target");

    center.Answer(surfaced.Id, true);
    var outcome = await authorization;
    Assert(outcome.IsAllowed && outcome.Decision == "allowed", "answering the task state releases the gate");
    Assert(audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5))
        .Any(r => r.ActionId == "file.delete" && r.Decision == "allowed"), "gate decision audited");

    // A stopped task cannot consent, so the gate must fail closed rather than hang.
    var withdrawnApproval = gate.AuthorizeAsync(
        new ActionRequest("file.delete", RiskCategory.DeleteOrOverwriteData, "再次删除", @"C:\work\cache2"));
    await WaitUntil(() => center.Pending().Count == 1, "second gate request pending");
    center.RequestStop(gated.Id);
    var closedOutcome = await withdrawnApproval;
    Assert(!closedOutcome.IsAllowed && closedOutcome.Decision == "approver-withdrew",
        "stopped task cannot approve, gate denies with an accurate reason");
    await WaitUntil(() => center.Pending().Count == 0, "stopped task interaction withdrawn");
}
finally
{
    try { Directory.Delete(auditDir, true); } catch (IOException) { }
}

Console.WriteLine($"All {checks} agent control checks passed.");