using System.Text.Json.Nodes;
using FairyAI_Android.Services.Knowledge;

namespace FairyAI_Android.Services.Tools;

/// <summary>
/// Answers a question from the user's imported documents. This is the tool that makes the knowledge
/// base useful from inside a conversation: it retrieves the passages itself, so the model does not
/// have to guess which document to search.
///
/// The answer always says which documents were used and which retriever found them. An answer that
/// cannot name its sources is a guess wearing a knowledge base's clothes.
/// </summary>
public sealed class AskDocumentsTool : ITool
{
    private readonly Func<string?, Task<RagAnswer>> _ask;

    public AskDocumentsTool(Func<string?, Task<RagAnswer>> ask) =>
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));

    public string Name => "ask_documents";

    public string Description => "在已导入知识库的文档里查资料并回答，答案会标出来源文档";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "question", Type = "string", Description = "要问的问题" },
    };

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var question = args?["question"]?.GetValue<string>();
        var answer = await _ask(question).ConfigureAwait(false);

        if (!answer.Ok) return answer.Text;

        var sources = answer.Sources.Count == 0
            ? "（没有可标注的来源）"
            : string.Join("、", answer.Sources);

        return $"{answer.Text}\n\n依据：{sources}（检索方式：{answer.Retrieval}）";
    }
}
