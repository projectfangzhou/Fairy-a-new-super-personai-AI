using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-desk-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    // ---------------------------------------------------------------- who may look

    {
        var center = new AgentControlCenter();
        var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens"));
        var enabled = false;
        var bridge = new PairedTaskBridge(center, tokens, () => enabled);

        var token = tokens.Issue("Pixel 8");

        Assert(bridge.RejectReason("Pixel 8", token) == SummarySyncAccess.DisabledCode,
            "the switch alone is not enough: off means off, even with a valid token");

        enabled = true;
        Assert(bridge.RejectReason("Pixel 8", token) is null, "enabled plus a valid token is allowed");
        Assert(bridge.RejectReason("Pixel 8", null) == SummarySyncAccess.UnpairedCode,
            "a device name without a token is refused");
        Assert(bridge.RejectReason("Other", token) == SummarySyncAccess.UnpairedCode,
            "another device's name cannot borrow this token");
    }

    // ---------------------------------------------------------------- what it shows

    {
        var center = new AgentControlCenter();
        var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens2"));
        var bridge = new PairedTaskBridge(center, tokens, () => true);

        var empty = bridge.Read();
        Assert(empty.Tasks.Count == 0 && empty.Pending.Count == 0, "an idle machine shows nothing");

        var task = center.Enqueue("整理 D:\\Dev\\客户报价\\2026.xlsx");
        task.Start();

        var view = bridge.Read();
        Assert(view.Tasks.Count == 1, "the task is listed");
        Assert(view.Tasks[0].TaskId == task.Id, "with its id");
        Assert(!view.Tasks[0].Title.Contains("客户报价", StringComparison.Ordinal),
            "a task title does not carry a path to the phone");
        Assert(view.Tasks[0].State.Length > 0, "and it carries a state");
    }

    // ---------------------------------------------------------------- answering goes through the same center

    {
        var center = new AgentControlCenter();
        var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens3"));
        var bridge = new PairedTaskBridge(center, tokens, () => true);

        var task = center.Enqueue("删除临时文件");
        task.Start();
        var pending = task.AskApprovalAsync("删除 D:\\Dev\\a.txt？", RiskCategory.DeleteOrOverwriteData);

        var desk = bridge.Read();
        Assert(desk.Pending.Count == 1, "the pending approval is visible");
        Assert(desk.Pending[0].Prompt.Contains("a.txt", StringComparison.Ordinal),
            "the prompt keeps the detail the user needs to decide");
        Assert(desk.Pending[0].Risk == nameof(RiskCategory.DeleteOrOverwriteData), "the risk is included");

        var interactionId = desk.Pending[0].InteractionId;
        var approved = bridge.Answer(interactionId, true);
        Assert(approved.Ok, "answering from the phone is accepted");
        Assert(await pending, "and the awaiting task on the PC really got a yes");

        var again = bridge.Answer(interactionId, true);
        Assert(!again.Ok && again.Message.Contains("不存在", StringComparison.Ordinal),
            "answering twice is reported, not silently accepted");
        Assert(bridge.Read().Pending.Count == 0, "the pending list is empty afterwards");
    }

    {
        var center = new AgentControlCenter();
        var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens4"));
        var bridge = new PairedTaskBridge(center, tokens, () => true);

        var task = center.Enqueue("确认部署目标");
        task.Start();
        var question = task.AskQuestionAsync("部署到哪个环境？staging 还是 prod？");

        var desk = bridge.Read();
        Assert(desk.Pending.Count == 1 && desk.Pending[0].Kind == nameof(PendingKind.Question),
            "a question is marked as a question");

        var blank = bridge.AnswerText(desk.Pending[0].InteractionId, "   ");
        Assert(!blank.Ok, "an empty answer is refused");

        var answered = bridge.AnswerText(desk.Pending[0].InteractionId, "staging");
        Assert(answered.Ok, "a text answer is accepted");
        Assert(await question == "staging", "and the PC's awaiting call received it");

        Assert(!bridge.Answer("nope", true).Ok, "an unknown interaction id is refused");
        Assert(!bridge.Answer("", true).Ok, "a missing interaction id is refused");
    }

    // ---------------------------------------------------------------- the JSON the phone actually parses

    {
        var center = new AgentControlCenter();
        var tokens = new PairedDeviceTokens(Path.Combine(directory, "tokens5"));
        var bridge = new PairedTaskBridge(center, tokens, () => true);

        var task = center.Enqueue("长任务");
        task.Start();

        var json = System.Text.Json.JsonSerializer.Serialize(bridge.Read());
        Assert(json.Contains("TaskId", StringComparison.Ordinal), "the view serialises its fields");
        Assert(!json.Contains("accessToken", StringComparison.OrdinalIgnoreCase), "no token leaks into the view");
        Assert(!json.Contains("refreshToken", StringComparison.OrdinalIgnoreCase), "and no refresh token");
    }

    Console.WriteLine($"All {checks} paired desk checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}
