using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FairyAI_Android.Models;
using FairyAI_Android.Services.Tools;

namespace FairyAI_Android.Services;

/// <summary>
/// Talks to an OpenAI-compatible endpoint that supports tool calling, and drives <see cref="ToolLoop"/>
/// with the result. One non-streaming request per round: the model either answers in text or asks for
/// tools, and the loop feeds the outputs back until it answers.
///
/// When the endpoint refuses the request because of the tools (older or non-tool providers return
/// 400/422 mentioning tools), the outcome says so, so the caller can fall back to plain streaming
/// instead of pretending tool calling works.
/// </summary>
public sealed class FunctionCallingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ToolRegistry _tools;

    public FunctionCallingService(ToolRegistry tools) =>
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    public int ToolCount => _tools.Count;

    public async Task<ToolLoopOutcome> RunAsync(
        string? systemPrompt,
        IReadOnlyList<ChatMessage>? history,
        string userMessage,
        Action<ToolCallResult>? onToolRun = null,
        int maxRounds = 6,
        CancellationToken cancellationToken = default)
    {
        var loop = new ToolLoop(_tools, AskAsync, maxRounds, onToolRun);
        return await loop.RunAsync(systemPrompt, history, userMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolLoopStep> AskAsync(IReadOnlyList<ToolTurn> turns, IReadOnlyList<object> definitions)
    {
        var config = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(config.LLM.ApiKey))
            return new ToolLoopStep(null, null, "还没有配置模型的 API Key", ToolsUnsupported: false);

        var payload = JsonSerializer.Serialize(new
        {
            model = config.LLM.Model,
            messages = turns.Select(SerializeTurn).ToArray(),
            tools = definitions,
            tool_choice = "auto",
            max_tokens = 1024,
            stream = false,
        }, JsonOptions);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.LLM.BaseUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.LLM.ApiKey}");

            response = await Http.SendAsync(request).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new ToolLoopStep(null, null, "模型接口超时");
        }
        catch (HttpRequestException ex)
        {
            return new ToolLoopStep(null, null, $"连不上模型接口：{ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var mentionsTools = body.Contains("tool", StringComparison.OrdinalIgnoreCase);
                var status = (int)response.StatusCode;
                var unsupported = mentionsTools && status is 400 or 404 or 422;

                return new ToolLoopStep(
                    null,
                    null,
                    $"模型接口返回 HTTP {status}",
                    ToolsUnsupported: unsupported);
            }

            return ParseReply(body);
        }
    }

    private static ToolLoopStep ParseReply(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return new ToolLoopStep(null, null, "模型返回里没有 choices");
            }

            var message = choices[0].GetProperty("message");

            // Providers that report usage put it here; when they do not, the counts stay zero rather
            // than being invented.
            var promptTokens = 0;
            var completionTokens = 0;
            if (document.RootElement.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var promptElement) &&
                    promptElement.ValueKind == JsonValueKind.Number)
                    promptTokens = promptElement.GetInt32();

                if (usage.TryGetProperty("completion_tokens", out var completionElement) &&
                    completionElement.ValueKind == JsonValueKind.Number)
                    completionTokens = completionElement.GetInt32();
            }

            string? content = message.TryGetProperty("content", out var contentElement) &&
                              contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

            List<ToolCall>? calls = null;
            if (message.TryGetProperty("tool_calls", out var callElements) &&
                callElements.ValueKind == JsonValueKind.Array)
            {
                calls = new List<ToolCall>();
                foreach (var item in callElements.EnumerateArray())
                {
                    if (!item.TryGetProperty("function", out var function)) continue;

                    var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var arguments = function.TryGetProperty("arguments", out var argumentsElement)
                        ? argumentsElement.GetString()
                        : null;

                    calls.Add(new ToolCall(
                        string.IsNullOrWhiteSpace(id) ? $"call_{calls.Count}" : id!,
                        name!,
                        string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments!));
                }

                if (calls.Count == 0) calls = null;
            }

            return new ToolLoopStep(content, calls, PromptTokens: promptTokens, CompletionTokens: completionTokens);
        }
        catch (JsonException)
        {
            return new ToolLoopStep(null, null, "模型返回不是合法 JSON");
        }
        catch (KeyNotFoundException)
        {
            return new ToolLoopStep(null, null, "模型返回缺少 message");
        }
    }

    private static object SerializeTurn(ToolTurn turn) => turn.Role switch
    {
        ToolTurnRole.System => new { role = "system", content = turn.Content ?? "" },

        ToolTurnRole.Tool => new { role = "tool", tool_call_id = turn.ToolCallId ?? "", content = turn.Content ?? "" },

        ToolTurnRole.Assistant when turn.Calls is { Count: > 0 } => new
        {
            role = "assistant",
            content = turn.Content,
            tool_calls = turn.Calls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.Arguments },
            }).ToArray(),
        },

        ToolTurnRole.Assistant => new { role = "assistant", content = turn.Content ?? "" },

        _ => new { role = "user", content = turn.Content ?? "" },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Android Capability Registry — declares all AI capabilities.
/// </summary>
public class CapabilityRegistry
{
    public static string BuildSystemPrompt(AppConfig config)
    {
        return @"你是仙灵，一个功能强大的个人 AI 助手。你拥有以下能力，根据用户需求自主选择工具：

## 核心能力
1. 屏幕自动化：截屏、点击、输入、按键
2. 语音合成：TTS 语音克隆
3. 知识库：文档分析、笔记管理
4. 联网搜索：Web 搜索
5. 系统工具：时间、计算、应用打开
6. 翻译：多语言翻译
7. 提醒：日程管理

## 人格设定
" + config.Personality.SystemPrompt;
    }

    public static string GetCapabilitiesJson(ToolRegistry tools)
    {
        return JsonSerializer.Serialize(new
        {
            name = "仙灵",
            version = "2.0.0",
            tools = tools.GetAll().Select(t => new { t.Name, t.Description }),
        });
    }
}
