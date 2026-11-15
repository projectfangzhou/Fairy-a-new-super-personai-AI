using System.Text.Json.Nodes;
using FairyAI_Android.Services.Knowledge;

namespace FairyAI_Android.Services.Tools;

/// <summary>Web search through whatever the app is already configured with (Tavily today).</summary>
public sealed class WebSearchTool : ITool
{
    private readonly ISearchService _search;

    public WebSearchTool(ISearchService search) => _search = search ?? throw new ArgumentNullException(nameof(search));

    public string Name => "web_search";

    public string Description => "联网搜索，返回几条结果摘要";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "query", Type = "string", Description = "搜索关键词" },
    };

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var query = args?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return "没有给出搜索关键词";

        var result = await _search.SearchAsync(query).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? "没有搜到结果" : result;
    }
}

/// <summary>Translation. Uses the LLM the app is already configured with; no extra service.</summary>
public sealed class TranslateTool : ITool
{
    private readonly TranslateService _translate;

    public TranslateTool(TranslateService translate) =>
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));

    public string Name => "translate";

    public string Description => "把一段文字翻译成目标语言";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "text", Type = "string", Description = "要翻译的文字" },
        new() { Name = "target", Type = "string", Description = "目标语言代码，例如 en、ja、zh-CN", Required = false },
    };

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var text = args?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return "没有给出要翻译的文字";

        var target = args?["target"]?.GetValue<string>();
        var translated = await _translate
            .TranslateAsync(text, string.IsNullOrWhiteSpace(target) ? "zh-CN" : target)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(translated) ? "翻译没有返回结果" : translated;
    }
}

/// <summary>
/// What the user has imported into the knowledge base. Read-only on purpose: importing is a user
/// action in settings, not something a model should decide.
/// </summary>
public sealed class KnowledgeListTool : ITool
{
    private readonly IKnowledgeSearch _knowledge;

    public KnowledgeListTool(IKnowledgeSearch knowledge) =>
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));

    public string Name => "knowledge_list";

    public string Description => "列出已经导入知识库的文档";

    public List<ToolParameter> Parameters => new();

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var files = _knowledge.Documents;
        return Task.FromResult(files.Count == 0
            ? "知识库里还没有文档"
            : string.Join("\n", files.Select(file => "- " + file)));
    }
}
