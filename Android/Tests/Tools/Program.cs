using System.Text.Json.Nodes;
using FairyAI_Android.Models;
using FairyAI_Android.Services;
using FairyAI_Android.Services.Screen;
using FairyAI_Android.Services.Markdown;
using FairyAI_Android.Services.Search;
using FairyAI_Android.Services.Usage;
using FairyAI_Android.Services.Compression;
using FairyAI_Android.Services.Tools;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

// ------------------------------------------------------------------ contracts

var registry = new ToolRegistry();
var clock = new CurrentTimeTool();
registry.Register(clock);

Assert(registry.GetTool("current_time") is not null, "a registered tool is found by name");
Assert(registry.GetTool("CURRENT_TIME") is not null, "tool lookup is case-insensitive");
Assert(registry.GetTool("nope") is null, "an unknown tool is null, not an exception");
Assert(registry.Count == 1, "the registry counts what it holds");
Assert(registry.Unregister("current_time") && registry.Count == 0, "a tool can be withdrawn");

try
{
    registry.Register(new NamelessTool());
    throw new Exception("FAIL: a nameless tool was accepted");
}
catch (ArgumentException)
{
    Assert(true, "a tool without a name is rejected at registration");
}

// ------------------------------------------------------------------ schema

var calcSchema = ToolSchema.BuildDefinition(new CalculateTool());
var schemaJson = System.Text.Json.JsonSerializer.Serialize(calcSchema);
using (var document = System.Text.Json.JsonDocument.Parse(schemaJson))
{
    var function = document.RootElement.GetProperty("function");
    Assert(document.RootElement.GetProperty("type").GetString() == "function", "the definition is a function");
    Assert(function.GetProperty("name").GetString() == "calculate", "the schema carries the tool name");
    Assert(function.GetProperty("parameters").GetProperty("required")[0].GetString() == "expression",
        "required parameters are listed");
    Assert(function.GetProperty("parameters").GetProperty("properties").TryGetProperty("expression", out var property) &&
           property.GetProperty("type").GetString() == "string",
        "the parameter type and description reach the model");
}

var emptySchema = System.Text.Json.JsonSerializer.Serialize(ToolSchema.BuildDefinition(new CurrentTimeTool()));
Assert(emptySchema.Contains("\"required\":[]", StringComparison.Ordinal),
    "a tool with no parameters still sends an empty required list");

// ------------------------------------------------------------------ dispatcher

var failures = new List<ToolCallResult>();
var results = await ToolDispatcher.RunAsync(
    BuildRegistry(out var executed),
    new[]
    {
        new ToolCall("1", "echo", "{\"value\":\"hi\"}"),
        new ToolCall("2", "missing", "{}"),
        new ToolCall("3", "broken", "{}"),
        new ToolCall("4", "echo", "not json"),
    },
    failures.Add);

Assert(results.Count == 4, "every call gets exactly one result");
Assert(results[0].Ok && results[0].Output == "hi", "a working tool returns its output");
Assert(executed.Count == 1, "only the call that made sense reached the tool");
Assert(!results[1].Ok && results[1].Output.Contains("没有名为", StringComparison.Ordinal),
    "an unknown tool is reported to the model, not thrown");
Assert(!results[2].Ok && results[2].Output.Contains("InvalidOperationException", StringComparison.Ordinal),
    "a tool that throws becomes a readable failure");
Assert(!results[3].Ok && results[3].Output.Contains("JSON", StringComparison.Ordinal),
    "unparseable arguments are refused before the tool runs");

// ------------------------------------------------------------------ the loop

{
    var turnsSeen = new List<IReadOnlyList<ToolTurn>>();
    var script = new Queue<ToolLoopStep>(new[]
    {
        new ToolLoopStep(null, new[] { new ToolCall("call_a", "echo", "{\"value\":\"42\"}") }),
        new ToolLoopStep("答案是 42", null),
    });

    var loop = new ToolLoop(
        BuildRegistry(out _),
        (turns, definitions) =>
        {
            turnsSeen.Add(turns.ToList());
            Assert(definitions.Count > 0, "tool definitions are offered on every round");
            return Task.FromResult(script.Dequeue());
        });

    var outcome = await loop.RunAsync("你是助手", new[] { new ChatMessage { Role = "user", Content = "之前的问题" } }, "现在的问题");

    Assert(outcome.Error is null, "a loop that gets an answer to text has no error");
    Assert(outcome.Text == "答案是 42", "the final text is the model's answer");
    Assert(outcome.Runs.Count == 1 && outcome.Runs[0].Output == "42", "the tool ran once with the right output");

    var finalTurns = turnsSeen[^1];
    Assert(finalTurns[0].Role == ToolTurnRole.System && finalTurns[0].Content == "你是助手", "the system prompt comes first");
    Assert(finalTurns[1].Role == ToolTurnRole.User && finalTurns[1].Content == "之前的问题", "history follows");
    Assert(finalTurns[2].Role == ToolTurnRole.User && finalTurns[2].Content == "现在的问题", "then the new message");
    Assert(finalTurns[3].Role == ToolTurnRole.Assistant && finalTurns[3].Calls!.Count == 1,
        "the assistant turn keeps the calls it made");
    Assert(finalTurns[4].Role == ToolTurnRole.Tool && finalTurns[4].ToolCallId == "call_a",
        "the tool turn answers the right call id");
}

{
    var loop = new ToolLoop(BuildRegistry(out _),
        (_, _) => Task.FromResult(new ToolLoopStep(null, null, "模型接口返回 HTTP 400", ToolsUnsupported: true)));

    var outcome = await loop.RunAsync(null, null, "问题");
    Assert(outcome.Error is not null && outcome.ToolsUnsupported,
        "a provider that refuses tools is reported as such, so the caller can fall back");
}

{
    var rounds = 0;
    var loop = new ToolLoop(BuildRegistry(out _), (_, _) =>
    {
        rounds++;
        return Task.FromResult(new ToolLoopStep(null, new[] { new ToolCall($"c{rounds}", "echo", "{\"value\":\"x\"}") }));
    }, maxRounds: 3);

    var outcome = await loop.RunAsync(null, null, "问题");
    Assert(rounds == 3, "the loop stops at the round budget instead of spinning");
    Assert(outcome.Text.Contains("轮次达到上限", StringComparison.Ordinal), "and says why it stopped");
    Assert(outcome.Runs.Count == 3, "every round's tool call is accounted for");
}

// ------------------------------------------------------------------ arithmetic

Assert(ArithmeticExpression.TryEvaluate("(12 + 3) * 4 / 2", out var value, out _) && Math.Abs(value - 30) < 1e-9,
    "parentheses and precedence are respected");
Assert(ArithmeticExpression.TryEvaluate("-3 + 10 % 4", out value, out _) && Math.Abs(value - (-1)) < 1e-9,
    "unary minus and modulo work");
Assert(ArithmeticExpression.TryEvaluate("2.5 * 4", out value, out _) && Math.Abs(value - 10) < 1e-9, "decimals work");
Assert(!ArithmeticExpression.TryEvaluate("1/0", out _, out var zeroError) && zeroError.Contains("零", StringComparison.Ordinal),
    "division by zero is an error, not infinity");
Assert(!ArithmeticExpression.TryEvaluate("2 +", out _, out _), "an incomplete expression is rejected");
Assert(!ArithmeticExpression.TryEvaluate("System.IO.File.Delete('x')", out _, out _),
    "anything that is not arithmetic is rejected — no code reaches the tool");
Assert(!ArithmeticExpression.TryEvaluate("", out _, out _), "an empty expression is rejected");

var calcOutput = await new CalculateTool().ExecuteAsync(new JsonObject { ["expression"] = "6*7" });
Assert(calcOutput == "42", "the calculate tool answers through the same evaluator");

var timeOutput = await new CurrentTimeTool().ExecuteAsync(new JsonObject());
Assert(timeOutput.Length > 8, "the clock tool returns something readable");

// ------------------------------------------------------------------ phone tools

{
    var bridge = new CountingBridge();
    var approval = new FakeApproval();
    var gate = new ScreenGate(bridge, approval,
        () => new HashSet<ScreenCapability> { ScreenCapability.Tap },
        () => true);

    var tool = new PhoneScreenTool(
        (action, package) => gate.RunAsync(action, package),
        ScreenCapability.Tap);

    var allowed = await tool.ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 200 });
    Assert(allowed.Contains("已执行", StringComparison.Ordinal), "an allowed tap reports that it ran");
    Assert(bridge.Injections == 1, "and the bridge was actually asked");
    Assert(bridge.LastAction?.X == 100 && bridge.LastAction?.Y == 200, "with the coordinates from the arguments");

    var refused = await tool.ExecuteAsync(new JsonObject { ["x"] = 1, ["y"] = 2, ["app_package"] = "com.eg.android.AlipayGphone" });
    Assert(refused.StartsWith("未执行", StringComparison.Ordinal), "a refused action says so to the model");
    Assert(refused.Contains("target_package_blocked", StringComparison.Ordinal),
        "and carries the reason code so it can be traced");
    Assert(bridge.Injections == 1, "a blocked target never reaches the bridge");

    var ungranted = new PhoneScreenTool(
        (action, package) => new ScreenGate(new CountingBridge(), new FakeApproval(), () => new HashSet<ScreenCapability>(), () => true)
            .RunAsync(action, package),
        ScreenCapability.Swipe);
    var notGranted = await ungranted.ExecuteAsync(new JsonObject { ["x"] = 0, ["y"] = 0, ["x2"] = 5, ["y2"] = 5 });
    Assert(notGranted.Contains("capability_not_granted", StringComparison.Ordinal),
        "a capability the user never allowed is refused by name");
}

// ------------------------------------------------------------------ search backends

{
    const string fixture = """
        <div class="result results_links">
          <a rel="nofollow" class="result__a" href="https://example.com/a">First &amp; best</a>
        </div>
        <div class="result results_links">
          <a rel="nofollow" class="result__a" href="https://example.com/a">First &amp; best</a>
        </div>
        <div class="result results_links">
          <a rel="nofollow" class="result__a" href="https://example.com/b">Second result</a>
        </div>
        """;

    var parsed = HtmlSearchBackend.Parse(fixture, 5);
    Assert(parsed.Count == 2, "duplicate links are collapsed and both real hits are kept");
    Assert(parsed[0].Title == "First & best", "html entities in titles are decoded");
    Assert(parsed[1].Url == "https://example.com/b", "the address is carried through");
    Assert(HtmlSearchBackend.Parse(fixture, 1).Count == 1, "the result limit is respected");
    Assert(HtmlSearchBackend.Parse("no results here", 5).Count == 0, "a page without results yields nothing");
    Assert(HtmlSearchBackend.Parse("", 5).Count == 0, "an empty page yields nothing");

    var first = new FakeSearchBackend("webview", _ => Array.Empty<SearchResult>());
    var second = new FakeSearchBackend("html", _ => new[] { new SearchResult { Title = "hit", Url = "https://x" } });
    var composite = new CompositeWebSearch(new[] { first, second });

    var primary = await composite.SearchAsync("query");
    Assert(primary.Count == 1 && composite.LastUsed == "html", "the composite moves on to the next backend and says which answered");
    Assert(first.Calls == 1 && second.Calls == 1, "both backends were asked exactly once");

    var throwing = new FakeSearchBackend("webview", _ => throw new HttpRequestException("boom"));
    var afterThrow = new CompositeWebSearch(new[] { throwing, second });
    var fromFallback = await afterThrow.SearchAsync("query");
    Assert(fromFallback.Count == 1 && afterThrow.LastUsed == "html", "a backend that throws is skipped, not fatal");

    var dead = new CompositeWebSearch(new[]
    {
        new FakeSearchBackend("webview", _ => Array.Empty<SearchResult>()),
        new FakeSearchBackend("html", _ => Array.Empty<SearchResult>()),
    });
    var nothing = await dead.SearchAsync("query");
    Assert(nothing.Count == 0 && dead.LastUsed is null, "when nobody answers the composite reports no source");

    var onlyOne = new FakeSearchBackend("webview", _ => new[] { new SearchResult { Title = "first", Url = "https://a" } });
    var counted = new FakeSearchBackend("html", _ => new[] { new SearchResult { Title = "second", Url = "https://b" } });
    var winner = new CompositeWebSearch(new[] { onlyOne, counted });
    var winnerResults = await winner.SearchAsync("query");
    Assert(winnerResults[0].Title == "first" && counted.Calls == 0,
        "the cheapest backend that answers stops the chain");
}

// ------------------------------------------------------------------ markdown

{
    var spans = MarkdownFormatter.Parse("**粗** 与 `代码` 和 [链接](https://example.com)");
    Assert(spans.Any(span => span.Bold && span.Text == "粗"), "bold becomes a bold span");
    Assert(spans.Any(span => span.Code && span.Text == "代码"), "inline code becomes a code span");
    Assert(spans.Any(span => span.Link == "https://example.com" && span.Text == "链接"), "a link keeps its text and target");
    Assert(MarkdownFormatter.ToPlainText("**粗** 与 `代码`") == "粗 与 代码", "the syntax characters are dropped, the words are not");

    var heading = MarkdownFormatter.Parse("## 标题");
    Assert(heading.Any(span => span.Bold && span.Text == "标题"), "a heading is bold text without the hashes");

    var list = MarkdownFormatter.Parse("- 第一条\n- 第二条");
    Assert(list.Count(span => span.Text == "• ") == 2, "list items get a bullet");
    Assert(list.Any(span => span.Text == "第一条"), "and keep their text");

    var fenced = MarkdownFormatter.Parse("说明\n```csharp\nvar x = 1;\n```\n结束");
    Assert(fenced.Any(span => span.Code && span.Text == "var x = 1;"), "a fenced block becomes a code span");
    Assert(fenced.Any(span => span.Text == "说明") && fenced.Any(span => span.Text == "结束"),
        "the lines around the fence survive");

    var italic = MarkdownFormatter.Parse("*斜*");
    Assert(italic.Any(span => span.Italic && span.Text == "斜"), "single asterisks are italic");

    Assert(MarkdownFormatter.Parse(null).Count == 0, "null markdown parses to nothing");
    Assert(MarkdownFormatter.Parse("").Count == 0, "empty markdown parses to nothing");

    var plain = MarkdownFormatter.Parse("普通的一句话，没有任何标记");
    Assert(plain.Count == 1 && plain[0].Text == "普通的一句话，没有任何标记", "text without markup is a single span");

    const string mixed = "**故障**：`input` 失败\n- 检查 Shizuku\n```\nexit 1\n```";
    var dropped = MarkdownFormatter.ToPlainText(mixed);
    Assert(dropped.Contains("故障", StringComparison.Ordinal) &&
           dropped.Contains("检查 Shizuku", StringComparison.Ordinal) &&
           dropped.Contains("exit 1", StringComparison.Ordinal),
        "no content is lost across mixed markdown");
}

// ------------------------------------------------------------------ token accounting

{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var ledger = new TokenLedger();
    Assert(ledger.Totals().Total == 0 && ledger.Totals().Calls == 0, "a fresh ledger is empty");

    ledger.Add(new TokenEntry(today, 100, 40, Exact: true));
    ledger.Add(new TokenEntry(today, 200, 60, Exact: false));
    var totals = ledger.Totals();
    Assert(totals.Total == 400 && totals.Calls == 2, "totals add prompt and completion across calls");
    Assert(ledger.Since(today).Total == 400, "today's window includes today's calls");
    Assert(ledger.Since(today.AddDays(1)).Total == 0, "a later window excludes them");
    Assert(ledger.ExactTotals().Total == 140, "reported numbers are counted separately from guesses");
    Assert(ledger.EstimatedTotals().Total == 260, "and the guesses are labelled as guesses");

    ledger.Add(new TokenEntry(today, -5, 10, Exact: true));
    Assert(ledger.Totals().Calls == 2, "a negative count is refused rather than corrupting the totals");

    var restored = TokenLedger.FromJson(ledger.ToJson());
    Assert(restored.Totals().Total == 400 && restored.Totals().Calls == 2, "the ledger survives a JSON round-trip");
    Assert(restored.ExactTotals().Total == 140, "and keeps which entries were exact");
    Assert(TokenLedger.FromJson("not json").Totals().Calls == 0, "a damaged file starts empty instead of throwing");
    Assert(TokenLedger.FromJson(null).Totals().Calls == 0, "a missing file starts empty");

    Assert(TokenLedger.EstimateTokens("abcd") == 1, "four latin characters are about one token");
    Assert(TokenLedger.EstimateTokens("abcdefgh") == 2, "eight latin characters are about two tokens");
    Assert(TokenLedger.EstimateTokens("你好世界") == 4, "each CJK character counts as a token");
    Assert(TokenLedger.EstimateTokens("") == 0 && TokenLedger.EstimateTokens(null) == 0, "empty text counts as zero");

    // ---------------------------------------------------------------- streaming chunks

    var (deltaText, deltaUsage) = StreamChunkParser.Parse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}");
    Assert(deltaText == "你好", "a delta chunk yields its text");
    Assert(deltaUsage is null, "a text delta carries no usage");

    var (roleText, _) = StreamChunkParser.Parse(
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}");
    Assert(roleText is null, "a role-only delta yields no text");

    var (nullText, _) = StreamChunkParser.Parse(
        "data: {\"choices\":[{\"delta\":{\"content\":null}}]}");
    Assert(nullText is null, "a null content yields no text");

    var (emptyText, _) = StreamChunkParser.Parse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"\"}}]}");
    Assert(emptyText is null, "an empty content yields no text");

    // The usage chunk arrives with an empty choices array — the shape a choices[0]-only parser drops.
    var (usageText, usageChunk) = StreamChunkParser.Parse(
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":34}}");
    Assert(usageText is null, "the usage chunk carries no text");
    Assert(usageChunk is not null && usageChunk.Exact, "the usage chunk yields exact counts");
    Assert(usageChunk!.PromptTokens == 120 && usageChunk.CompletionTokens == 34, "both counts are read");
    Assert(usageChunk.TotalTokens == 154, "the total is the sum");

    var (_, zeroUsage) = StreamChunkParser.Parse(
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}");
    Assert(zeroUsage is null, "a zero usage chunk is not recorded as an exact zero");

    var (_, noChoices) = StreamChunkParser.Parse(
        "data: {\"id\":\"x\",\"usage\":{\"prompt_tokens\":7}}");
    Assert(noChoices!.PromptTokens == 7 && noChoices.CompletionTokens == 0,
        "a missing completion count reads as zero, not as a failure");

    Assert(StreamChunkParser.IsDone("data: [DONE]"), "the done marker is recognised");
    Assert(!StreamChunkParser.IsDone("data: {\"choices\":[]}"), "a normal chunk is not the done marker");

    var (doneText, doneUsage) = StreamChunkParser.Parse("data: [DONE]");
    Assert(doneText is null && doneUsage is null, "the done marker parses to nothing");

    var (keepAliveText, keepAliveUsage) = StreamChunkParser.Parse(": ping");
    Assert(keepAliveText is null && keepAliveUsage is null, "a keep-alive line parses to nothing");

    var (brokenText, brokenUsage) = StreamChunkParser.Parse("data: {not json");
    Assert(brokenText is null && brokenUsage is null, "a malformed chunk is skipped instead of throwing");

    var (blankText, blankUsage) = StreamChunkParser.Parse("   ");
    Assert(blankText is null && blankUsage is null, "a blank line parses to nothing");

    // The estimate path: a provider that sends no usage still gets counted, and says it is a guess.
    var guess = new TokenUsage(TokenLedger.EstimateTokens("abcd"), TokenLedger.EstimateTokens("你好"), Exact: false);
    Assert(guess.PromptTokens == 1 && guess.CompletionTokens == 2, "an estimate uses the same token heuristic");
    Assert(!guess.Exact, "an estimate is never labelled exact");

    // ---------------------------------------------------------------- context window

    List<ChatMessage> Conversation(int count, int charsEach) =>
        Enumerable.Range(0, count)
            .Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = new string('字', charsEach) })
            .ToList();

    var shortChat = Conversation(4, 5);
    Assert(!ContextWindow.ShouldCompress(shortChat, 8_000), "a short conversation is sent as it is");
    Assert(ContextWindow.Plan(shortChat, 8_000).SummarizeCount == 0, "a short conversation has nothing to fold");

    var longChat = Conversation(40, 400);
    var longPlan = ContextWindow.Plan(longChat, 8_000);
    Assert(longPlan.Needed, "a conversation over the budget is folded");
    Assert(longPlan.KeepCount == ContextWindow.KeepRecent, "the recent turns are kept verbatim");
    Assert(longPlan.SummarizeCount == longChat.Count - ContextWindow.KeepRecent, "everything older is summarised");
    Assert(longPlan.EstimatedTokens >= 8_000, "the estimate is what triggered it");

    Assert(!ContextWindow.ShouldCompress(longChat, 0), "a zero budget means compression is off, not 'compress everything'");
    Assert(!ContextWindow.ShouldCompress(longChat, -1), "a negative budget is off too");
    Assert(ContextWindow.ShouldCompress(longChat, longPlan.EstimatedTokens), "being exactly at the budget counts as over it");

    var applied = ContextWindow.Apply("用户想订周五的机票", longChat, longPlan);
    Assert(applied[0].Role == "system", "the summary is a system message");
    Assert(applied[0].Content.StartsWith(ContextWindow.SummaryMarker, StringComparison.Ordinal),
        "the summary carries the marker so it can be folded again later");
    Assert(applied[0].Content.Contains("周五的机票", StringComparison.Ordinal), "the summary text is kept");
    Assert(applied.Count == 1 + ContextWindow.KeepRecent, "the result is one summary plus the recent turns");
    Assert(applied[^1].Content == longChat[^1].Content, "the newest message is the last one sent");
    Assert(applied[^2].Content == longChat[^2].Content, "order is preserved");

    var alreadyMarked = ContextWindow.Apply(ContextWindow.SummaryMarker + " 旧摘要", longChat, longPlan);
    Assert(alreadyMarked[0].Content == ContextWindow.SummaryMarker + " 旧摘要", "a summary that already has the marker is not marked twice");

    var empty = ContextWindow.Apply("", longChat, longPlan);
    Assert(empty.Count == ContextWindow.KeepRecent, "an empty summary adds no system message");

    var prompt = ContextWindow.BuildPrompt(longChat.Take(5).ToList());
    Assert(prompt.Contains("摘要", StringComparison.Ordinal), "the prompt asks for a summary");
    Assert(prompt.Contains("user:", StringComparison.Ordinal), "the prompt keeps who said what");
    Assert(prompt.Contains("不要编造", StringComparison.Ordinal), "the prompt forbids making things up");

    var withPreviousSummary = ContextWindow.BuildPrompt(new List<ChatMessage>
    {
        new() { Role = "system", Content = ContextWindow.SummaryMarker + " 之前聊过机票" },
        new() { Role = "user", Content = "" },
    });
    Assert(withPreviousSummary.Contains("此前的摘要", StringComparison.Ordinal),
        "an earlier summary is labelled instead of pretending to be dialogue");
    Assert(!withPreviousSummary.Contains("system:", StringComparison.Ordinal),
        "the earlier summary is not relabelled as a system line");

    // Two 10-character messages plus the newline they are joined with.
    Assert(ContextWindow.EstimateTokens(Conversation(2, 10)) == 21, "the estimate follows the ledger heuristic");
    Assert(ContextWindow.EstimateTokens(null!) == 0, "no history estimates as zero");
    Assert(ContextWindow.Apply("x", new List<ChatMessage>(), new CompressionPlan(0, 0, 0)).Count == 1,
        "an empty history leaves only the summary");

    Assert(ContextWindow.LooksLikeFailure(""), "an empty summary counts as no summary");
    Assert(ContextWindow.LooksLikeFailure("Please configure LLM API Key in settings"),
        "a missing-key message is not a summary");
    Assert(ContextWindow.LooksLikeFailure("LLM request failed"), "a failed request is not a summary");
    Assert(!ContextWindow.LooksLikeFailure("用户想订周五的机票"), "a real summary is accepted");
    Assert(!ContextWindow.LooksLikeFailure("我们讨论了提醒功能，用户希望每天 9 点提醒吃药。"),
        "a summary that mentions the app's own features is still a summary");

    // The loop must add up what the provider reported, and add nothing when it reported nothing.
    var usageScript = new Queue<ToolLoopStep>(new[]
    {
        new ToolLoopStep(null, new[] { new ToolCall("c1", "echo", "{\"value\":\"a\"}") }, PromptTokens: 30, CompletionTokens: 5),
        new ToolLoopStep("done", null, PromptTokens: 45, CompletionTokens: 10),
    });

    var usageLoop = new ToolLoop(BuildRegistry(out _), (_, _) => Task.FromResult(usageScript.Dequeue()));
    var usageOutcome = await usageLoop.RunAsync(null, null, "问题");
    Assert(usageOutcome.PromptTokens == 75 && usageOutcome.CompletionTokens == 15,
        "usage is summed across the rounds of one answer");

    var silentLoop = new ToolLoop(BuildRegistry(out _), (_, _) => Task.FromResult(new ToolLoopStep("done", null)));
    var silentOutcome = await silentLoop.RunAsync(null, null, "问题");
    Assert(silentOutcome.PromptTokens == 0 && silentOutcome.CompletionTokens == 0,
        "a provider that reports no usage yields zero, not an invented number");
}

Console.WriteLine();
Console.WriteLine($"All {checks} checks passed.");
return 0;

static ToolRegistry BuildRegistry(out List<string> executed)
{
    var log = new List<string>();
    executed = log;

    var registry = new ToolRegistry();
    registry.Register(new EchoTool(log));
    registry.Register(new ThrowingTool());
    return registry;
}

sealed class EchoTool : ITool
{
    private readonly List<string> _log;

    public EchoTool(List<string> log) => _log = log;

    public string Name => "echo";

    public string Description => "echoes the value back";

    public List<ToolParameter> Parameters => new() { new() { Name = "value", Description = "what to echo" } };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var value = args?["value"]?.GetValue<string>() ?? "";
        _log.Add(value);
        return Task.FromResult(value);
    }
}

sealed class ThrowingTool : ITool
{
    public string Name => "broken";

    public string Description => "always throws";

    public List<ToolParameter> Parameters => new();

    public Task<string> ExecuteAsync(JsonObject args) => throw new InvalidOperationException("nope");
}

sealed class NamelessTool : ITool
{
    public string Name => "";

    public string Description => "";

    public List<ToolParameter> Parameters => new();

    public Task<string> ExecuteAsync(JsonObject args) => Task.FromResult("");
}

sealed class CountingBridge : IShizukuBridge
{
    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public int Injections { get; private set; }

    public ScreenAction? LastAction { get; private set; }

    public ShizukuProbeResult Probe() => new(ShizukuAvailability.Authorized, "1.0", "fake");

    public bool OpenShizukuApp(out string message)
    {
        message = "fake";
        return true;
    }

    public Task<ScreenExecutionResult> InjectAsync(ScreenAction action, CancellationToken cancellationToken = default)
    {
        Injections++;
        LastAction = action;
        return Task.FromResult(ScreenExecutionResult.Ok());
    }
}

sealed class FakeApproval : IScreenApproval
{
    public Task<bool> AskAsync(string capabilityLabel, string description, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

sealed class FakeSearchBackend : IWebSearchBackend
{
    private readonly Func<string, IReadOnlyList<SearchResult>> _answer;

    public FakeSearchBackend(string name, Func<string, IReadOnlyList<SearchResult>> answer)
    {
        Name = name;
        _answer = answer;
    }

    public string Name { get; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_answer(query));
    }
}
