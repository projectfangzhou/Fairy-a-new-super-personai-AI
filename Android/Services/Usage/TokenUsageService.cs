using System.IO;

namespace FairyAI_Android.Services.Usage;

/// <summary>
/// File-backed token accounting. Records what the provider actually reported; when a call cannot report
/// usage, the caller may pass an estimate, which is stored flagged as one.
/// </summary>
public sealed class TokenUsageService
{
    private static readonly string FilePath =
        Path.Combine(FileSystem.AppDataDirectory, "token_usage.json");

    private readonly object _sync = new();
    private TokenLedger? _ledger;

    public TokenLedger Ledger
    {
        get
        {
            lock (_sync) return _ledger ??= TokenLedger.FromJson(ReadFile());
        }
    }

    /// <summary>Records a call. <paramref name="exact"/> must be false when the numbers are guesses.</summary>
    public void Record(int promptTokens, int completionTokens, bool exact)
    {
        if (promptTokens <= 0 && completionTokens <= 0) return;

        lock (_sync)
        {
            Ledger.Add(new TokenEntry(DateOnly.FromDateTime(DateTime.Now), promptTokens, completionTokens, exact));
            Save();
        }
    }

    public void RecordEstimate(string? prompt, string? completion) =>
        Record(TokenLedger.EstimateTokens(prompt), TokenLedger.EstimateTokens(completion), exact: false);

    public TokenTotals Today()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        lock (_sync) return Ledger.Since(today);
    }

    public TokenTotals Total()
    {
        lock (_sync) return Ledger.Totals();
    }

    public void Clear()
    {
        lock (_sync)
        {
            Ledger.Clear();
            Save();
        }
    }

    /// <summary>Human-readable line for the dashboard; says plainly how much of it is a guess.</summary>
    public string Describe()
    {
        lock (_sync)
        {
            var today = Ledger.Since(DateOnly.FromDateTime(DateTime.Now));
            var total = Ledger.Totals();
            var estimated = Ledger.EstimatedTotals();

            if (total.Calls == 0) return "还没有记录到 token 用量（需要模型接口返回 usage）";

            var text = $"今日 {today.Total:N0} · 累计 {total.Total:N0}（{total.Calls} 次调用）";
            if (estimated.Calls > 0)
                text += $" · 其中 {estimated.Total:N0} 是按字数估算的，不是接口报告值";

            return text;
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, Ledger.ToJson());
        }
        catch
        {
            // Losing the counters is not worth an exception in the middle of a conversation.
        }
    }

    private static string? ReadFile()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        }
        catch
        {
            return null;
        }
    }
}
