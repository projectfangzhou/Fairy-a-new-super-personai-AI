using System.Text.Json;
using System.Text.Json.Nodes;
using FairyAI_Android.Models;

namespace FairyAI_Android.Services.Tools;

public enum ToolTurnRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// One message in the tool conversation. Assistant turns carry the calls the model made; tool turns
/// carry the id they answer, because the API rejects a result that cannot be matched to a call.
/// </summary>
public sealed record ToolTurn(
    ToolTurnRole Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<ToolCall>? Calls = null)
{
    public static ToolTurn System(string content) => new(ToolTurnRole.System, content);

    public static ToolTurn User(string content) => new(ToolTurnRole.User, content);

    public static ToolTurn Assistant(string? content, IReadOnlyList<ToolCall> calls) =>
        new(ToolTurnRole.Assistant, content, null, calls);

    public static ToolTurn Tool(string toolCallId, string content) =>
        new(ToolTurnRole.Tool, content, toolCallId);
}

/// <summary>What came back from one model call: text, calls, or the reason it could not answer.</summary>
public sealed record ToolLoopStep(
    string? Content,
    IReadOnlyList<ToolCall>? Calls,
    string? Error = null,
    bool ToolsUnsupported = false,
    int PromptTokens = 0,
    int CompletionTokens = 0);

public sealed record ToolCallResult(string Id, string Name, string Arguments, string Output, bool Ok);

public sealed record ToolLoopOutcome(
    string Text,
    IReadOnlyList<ToolCallResult> Runs,
    bool ToolsUnsupported,
    string? Error,
    int PromptTokens = 0,
    int CompletionTokens = 0);

/// <summary>JSON schema derived from <see cref="ITool.Parameters"/>, so the model knows what to send.</summary>
public static class ToolSchema
{
    public static object BuildDefinition(ITool tool)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var parameter in tool.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name)) continue;

            properties[parameter.Name] = new
            {
                type = string.IsNullOrWhiteSpace(parameter.Type) ? "string" : parameter.Type,
                description = parameter.Description ?? "",
            };

            if (parameter.Required) required.Add(parameter.Name);
        }

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description ?? "",
                parameters = new
                {
                    type = "object",
                    properties,
                    required,
                },
            },
        };
    }

    public static IReadOnlyList<object> BuildDefinitions(IEnumerable<ITool> tools) =>
        tools.Select(BuildDefinition).ToList();
}

/// <summary>
/// Runs the calls a model asked for. A missing tool, bad arguments or a throwing tool become a result
/// the model can read — never an exception that kills the conversation.
/// </summary>
public static class ToolDispatcher
{
    public static async Task<IReadOnlyList<ToolCallResult>> RunAsync(
        ToolRegistry registry,
        IEnumerable<ToolCall> calls,
        Action<ToolCallResult>? onResult = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ToolCallResult>();

        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RunOneAsync(registry, call, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onResult?.Invoke(result);
        }

        return results;
    }

    private static async Task<ToolCallResult> RunOneAsync(
        ToolRegistry registry,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        var tool = registry.GetTool(call.Name);
        if (tool is null)
            return new ToolCallResult(call.Id, call.Name, call.Arguments, $"没有名为 {call.Name} 的工具", false);

        JsonObject arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(call.Arguments)
                ? new JsonObject()
                : JsonNode.Parse(call.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new ToolCallResult(call.Id, call.Name, call.Arguments, "参数不是合法 JSON，未执行", false);
        }

        try
        {
            var output = await tool.ExecuteAsync(arguments).ConfigureAwait(false);
            return new ToolCallResult(call.Id, call.Name, call.Arguments, output ?? "", true);
        }
        catch (OperationCanceledException)
        {
            return new ToolCallResult(call.Id, call.Name, call.Arguments, "操作被取消", false);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, call.Arguments, $"工具出错：{ex.GetType().Name}", false);
        }
    }
}

/// <summary>
/// The tool-calling loop: ask, run what it asked for, feed the results back, repeat until it answers in
/// text or the round budget runs out. The model call is injected so the whole loop is testable with a
/// scripted fake and never depends on a network.
/// </summary>
public sealed class ToolLoop
{
    private readonly ToolRegistry _registry;
    private readonly Func<IReadOnlyList<ToolTurn>, IReadOnlyList<object>, Task<ToolLoopStep>> _ask;
    private readonly int _maxRounds;
    private readonly Action<ToolCallResult>? _onToolRun;

    public ToolLoop(
        ToolRegistry registry,
        Func<IReadOnlyList<ToolTurn>, IReadOnlyList<object>, Task<ToolLoopStep>> ask,
        int maxRounds = 6,
        Action<ToolCallResult>? onToolRun = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        if (maxRounds <= 0) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        _maxRounds = maxRounds;
        _onToolRun = onToolRun;
    }

    public async Task<ToolLoopOutcome> RunAsync(
        string? systemPrompt,
        IEnumerable<ChatMessage>? history,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var turns = new List<ToolTurn>();

        if (!string.IsNullOrWhiteSpace(systemPrompt)) turns.Add(ToolTurn.System(systemPrompt));
        if (history is not null)
        {
            foreach (var message in history)
            {
                if (string.IsNullOrWhiteSpace(message.Content)) continue;
                turns.Add(new ToolTurn(
                    string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? ToolTurnRole.Assistant
                        : ToolTurnRole.User,
                    message.Content));
            }
        }

        turns.Add(ToolTurn.User(userMessage));

        var definitions = ToolSchema.BuildDefinitions(_registry.GetAll());
        var runs = new List<ToolCallResult>();
        string? lastContent = null;
        var promptTokens = 0;
        var completionTokens = 0;

        for (var round = 0; round < _maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ToolLoopStep step;
            try
            {
                step = await _ask(turns, definitions).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ToolLoopOutcome(lastContent ?? "", runs, false, "已取消", promptTokens, completionTokens);
            }
            catch (Exception ex)
            {
                return new ToolLoopOutcome(lastContent ?? "", runs, false, ex.Message, promptTokens, completionTokens);
            }

            if (step.Error is not null)
                return new ToolLoopOutcome(
                    lastContent ?? "", runs, step.ToolsUnsupported, step.Error, promptTokens, completionTokens);

            lastContent = step.Content;
            promptTokens += step.PromptTokens;
            completionTokens += step.CompletionTokens;

            if (step.Calls is null || step.Calls.Count == 0)
                return new ToolLoopOutcome(step.Content ?? "", runs, false, null, promptTokens, completionTokens);

            turns.Add(ToolTurn.Assistant(step.Content, step.Calls));

            var results = await ToolDispatcher
                .RunAsync(_registry, step.Calls, _onToolRun, cancellationToken)
                .ConfigureAwait(false);

            runs.AddRange(results);

            foreach (var result in results)
                turns.Add(ToolTurn.Tool(result.Id, result.Output));
        }

        return new ToolLoopOutcome(
            (lastContent ?? "") + "\n\n（工具调用轮次达到上限，先停在这里）",
            runs,
            false,
            null,
            promptTokens,
            completionTokens);
    }
}
