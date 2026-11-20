using System.IO;
using FairyAI_Android.Models;
using FairyAI_Android.Services.Compression;

namespace FairyAI_Android.Services;

/// <summary>
/// Keeps the request to the model inside its budget by folding older messages into one summary.
///
/// Two things are deliberate. The visible transcript is not touched — see <see cref="ContextWindow"/>.
/// And nothing here fails silently: if the summary cannot be produced, the full history is sent as it
/// is, and the reason is written to fairy.log so "why did the model forget" has an answer.
/// </summary>
public class ContextCompressionService
{
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    private readonly ILlmService _llm;

    public ContextCompressionService(ILlmService llm) =>
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    /// <summary>How many times a summary was produced, for the dashboard.</summary>
    public int Compressions { get; private set; }

    public string? LastError { get; private set; }

    public bool ShouldCompress(IReadOnlyList<ChatMessage> history)
    {
        var config = ConfigManager.Load().Context;
        if (!config.Enabled) return false;

        return ContextWindow.ShouldCompress(history, config.AutoCompressTokens);
    }

    /// <summary>
    /// The history to actually send. Returns the input unchanged when compression is off, unnecessary,
    /// or impossible right now; either way the caller can just use the result.
    /// </summary>
    public async Task<List<ChatMessage>> BuildRequestHistoryAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var config = ConfigManager.Load().Context;
        if (!config.Enabled) return history?.ToList() ?? new List<ChatMessage>();

        var messages = history?.ToList() ?? new List<ChatMessage>();
        var plan = ContextWindow.Plan(messages, config.AutoCompressTokens);
        if (!plan.Needed) return messages;

        var older = messages.Take(plan.SummarizeCount).ToList();
        var summary = new System.Text.StringBuilder();

        try
        {
            try
            {
                await foreach (var chunk in _llm
                    .StreamChatAsync(new List<ChatMessage>(), ContextWindow.BuildPrompt(older))
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                    summary.Append(chunk);
            }
            catch (OperationCanceledException)
            {
                LastError = "压缩历史时被取消";
                Log(LastError);
                return messages;
            }
        }
        catch (Exception ex)
        {
            // Sending the whole history is worse for the budget but better than losing the thread.
            LastError = $"压缩历史失败：{ex.GetType().Name}";
            Log(LastError);
            return messages;
        }

        var text = summary.ToString().Trim();
        if (ContextWindow.LooksLikeFailure(text))
        {
            LastError = "压缩历史失败：模型没有给出可用的摘要（" + text + "）";
            Log(LastError);
            return messages;
        }

        Compressions++;
        LastError = null;
        Log($"folded {plan.SummarizeCount} messages (~{plan.EstimatedTokens} tokens) into a summary, kept {plan.KeepCount}");

        return ContextWindow.Apply(text, messages, plan);
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [CTX] {msg}\n"); } catch { }
    }
}
