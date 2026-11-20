namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Builds the prompt for a grounded answer.
///
/// The instructions matter more than they look. A model handed a few paragraphs will happily answer
/// from its own memory and cite nothing, which is exactly the failure mode a knowledge base is
/// supposed to prevent. So the prompt numbers the sources, demands the number be cited, and states
/// what to do when the sources do not answer the question.
/// </summary>
public static class RagPrompt
{
    public const string NoContextAnswer =
        "知识库里没有找到和这个问题相关的内容。可以先把相关文档导入知识库，或者换个说法再问一次。";

    public static string Build(string question, IReadOnlyList<RetrievalHit> hits)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("只依据下面编号的资料回答问题。");
        builder.AppendLine("规则：");
        builder.AppendLine("1. 能回答就在句末标出来源编号，例如「…（资料 2）」。");
        builder.AppendLine("2. 资料里没有写的内容，明确说「资料里没有提到」，不要用你自己的知识补全。");
        builder.AppendLine("3. 如果资料之间矛盾，把两种说法都列出来并各自标号。");
        builder.AppendLine();

        var index = 0;
        foreach (var hit in hits ?? Array.Empty<RetrievalHit>())
        {
            builder.Append("资料 ").Append(++index).Append('：').AppendLine(hit.Document);
            builder.AppendLine(hit.Passage.Trim());
            builder.AppendLine();
        }

        builder.Append("问题：").AppendLine((question ?? "").Trim());
        return builder.ToString();
    }

    /// <summary>The document names that actually took part, in order, without repeats.</summary>
    public static IReadOnlyList<string> Sources(IReadOnlyList<RetrievalHit> hits)
    {
        var names = new List<string>();
        foreach (var hit in hits ?? Array.Empty<RetrievalHit>())
        {
            if (string.IsNullOrWhiteSpace(hit.Document)) continue;
            if (names.Contains(hit.Document, StringComparer.Ordinal)) continue;
            names.Add(hit.Document);
        }

        return names;
    }

    /// <summary>Which retrievers contributed — so an answer can say how it was found.</summary>
    public static string DescribeSources(IReadOnlyList<RetrievalHit> hits)
    {
        var usedKeyword = false;
        var usedVector = false;

        foreach (var hit in hits ?? Array.Empty<RetrievalHit>())
        {
            usedKeyword |= hit.Source is RetrievalSource.Keyword or RetrievalSource.Both;
            usedVector |= hit.Source is RetrievalSource.Vector or RetrievalSource.Both;
        }

        if (usedKeyword && usedVector) return "关键词 + 向量";
        if (usedVector) return "向量";
        if (usedKeyword) return "关键词";
        return "没有命中";
    }
}
