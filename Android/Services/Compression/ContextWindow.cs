using FairyAI_Android.Models;
using FairyAI_Android.Services.Usage;

namespace FairyAI_Android.Services.Compression;

/// <summary>What compressing an over-long conversation would do.</summary>
public sealed record CompressionPlan(int SummarizeCount, int KeepCount, int EstimatedTokens)
{
    public bool Needed => SummarizeCount > 0;
}

/// <summary>
/// Decides what gets sent to the model when a conversation grows past the budget.
///
/// Pure on purpose: the arithmetic that decides "summarise these, keep those" is the part worth
/// asserting on, and it has to be independent of any LLM call so a test can run it.
///
/// One rule shapes all of it: the visible transcript is never rewritten. Compressing is something done
/// to the request, not to what the user sees — a chat that quietly loses its earlier messages looks
/// like a bug to the person who wrote them.
/// </summary>
public static class ContextWindow
{
    /// <summary>Messages kept verbatim at the end of the conversation.</summary>
    public const int KeepRecent = 6;

    /// <summary>Marker the summary message carries, so a second round can fold it instead of stacking.</summary>
    public const string SummaryMarker = "[对话摘要]";

    /// <summary>Estimate for the whole conversation, in tokens, using the same heuristic the ledger uses.</summary>
    public static int EstimateTokens(IEnumerable<ChatMessage> history) =>
        TokenLedger.EstimateTokens(string.Join('\n', (history ?? Enumerable.Empty<ChatMessage>())
            .Select(message => message?.Content ?? "")));

    public static bool ShouldCompress(IReadOnlyList<ChatMessage>? history, int thresholdTokens) =>
        Plan(history, thresholdTokens).Needed;

    public static CompressionPlan Plan(IReadOnlyList<ChatMessage>? history, int thresholdTokens)
    {
        var messages = history ?? Array.Empty<ChatMessage>();
        var tokens = EstimateTokens(messages);

        // Nothing to fold, or already small enough: leave it alone. The threshold being <= 0 means
        // "compression is off", which is how the caller switches it off.
        if (thresholdTokens <= 0 || messages.Count <= KeepRecent) return new CompressionPlan(0, messages.Count, tokens);
        if (tokens < thresholdTokens) return new CompressionPlan(0, messages.Count, tokens);

        return new CompressionPlan(messages.Count - KeepRecent, Math.Min(KeepRecent, messages.Count), tokens);
    }

    /// <summary>
    /// The prompt that summarises the older part. Role labels are kept so the summary knows who said
    /// what, and the previous summary (if any) is labelled as such rather than passed off as dialogue.
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<ChatMessage> older)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("把下面这段对话压缩成一份摘要，保留事实、结论、用户的偏好与未完成的事项。");
        builder.AppendLine("不要编造，不要加评价，直接输出摘要正文。");
        builder.AppendLine();

        foreach (var message in older ?? Array.Empty<ChatMessage>())
        {
            var content = (message?.Content ?? "").Trim();
            if (content.Length == 0) continue;

            var isSummary = content.StartsWith(SummaryMarker, StringComparison.Ordinal);
            var role = isSummary ? "此前的摘要" : message!.Role;
            builder.Append(role).Append(": ").AppendLine(content);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a "summary" is really a failure message. The streaming API reports some failures as
    /// ordinary text ("LLM request failed", "Please configure LLM API Key in settings"), and those
    /// must never become the conversation's memory — a summary that says the key is missing would be
    /// carried into every later turn as if it were something the user said.
    /// </summary>
    public static bool LooksLikeFailure(string? summary)
    {
        var text = (summary ?? "").Trim();
        if (text.Length == 0) return true;

        return text.StartsWith("Please configure", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("LLM request failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the history to send: one summary message followed by the recent messages. The summary
    /// is whatever the caller produced — this only decides the shape.
    /// </summary>
    public static List<ChatMessage> Apply(string summary, IReadOnlyList<ChatMessage> history, CompressionPlan plan)
    {
        var messages = history ?? Array.Empty<ChatMessage>();
        var result = new List<ChatMessage>();

        var text = (summary ?? "").Trim();
        if (text.Length > 0)
        {
            if (!text.StartsWith(SummaryMarker, StringComparison.Ordinal)) text = SummaryMarker + " " + text;
            result.Add(new ChatMessage { Role = "system", Content = text });
        }

        var keep = Math.Max(0, Math.Min(plan.KeepCount, messages.Count));
        if (keep > 0) result.AddRange(messages.Skip(messages.Count - keep));

        return result;
    }
}
