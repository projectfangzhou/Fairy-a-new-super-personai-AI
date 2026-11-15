namespace MyAiAssistant.Services.Relay;

/// <summary>Why a limit applies, so the UI can say where the number came from.</summary>
public enum QuotaSource
{
    /// <summary>Public free-tier figures; used until the account's real limits are known.</summary>
    FreeTierDefault = 0,

    /// <summary>The user entered their own limits after checking the account.</summary>
    UserConfigured
}

/// <summary>Limits applied to one relayed share.</summary>
public sealed record RelayQuota(long SingleFileLimitBytes, long ShareTotalLimitBytes, QuotaSource Source)
{
    /// <summary>
    /// R2's free tier is 10 GB-month of storage, which is a storage budget, not a per-share
    /// budget. These two numbers are deliberately smaller than that: a share must never be able
    /// to consume the whole free allowance in one go, and 24-hour retention doubles nothing but
    /// still costs storage while it lives.
    /// </summary>
    public static RelayQuota FreeTier { get; } =
        new(2L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, QuotaSource.FreeTierDefault);

    public string Describe() => Source == QuotaSource.FreeTierDefault
        ? "按 Cloudflare 免费额度估算的硬上限"
        : "按你填写的账户额度";
}

public sealed record RelayQuotaDecision(bool Allowed, string Reason, RelayQuota Quota)
{
    public static RelayQuotaDecision Refuse(string reason, RelayQuota quota) => new(false, reason, quota);
    public static RelayQuotaDecision Permit(string reason, RelayQuota quota) => new(true, reason, quota);
}

/// <summary>
/// Decides whether a share may start under the configured relay limits.
///
/// It answers before the upload begins, not halfway through: the project rule is that reaching
/// a quota must be a clear refusal up front, never a transfer that dies at 80% with the user's
/// data partly on someone else's storage.
/// </summary>
public sealed class RelayQuotaPolicy
{
    private readonly RelayQuota _quota;

    public RelayQuotaPolicy(RelayQuota? quota = null)
    {
        _quota = quota ?? RelayQuota.FreeTier;
    }

    public RelayQuota Quota => _quota;

    /// <summary>
    /// Checks one planned share. <paramref name="usedBytes"/> is what the account already
    /// holds for relayed files, so a user who has filled their allowance is told so before
    /// uploading again.
    /// </summary>
    public RelayQuotaDecision Evaluate(IReadOnlyList<long> fileSizes, long usedBytes = 0, long storageLimitBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(fileSizes);

        if (fileSizes.Count == 0)
            return RelayQuotaDecision.Refuse("没有要中转的文件", _quota);

        if (fileSizes.Any(size => size < 0))
            return RelayQuotaDecision.Refuse("文件大小无效", _quota);

        var oversize = fileSizes.Where(size => size > _quota.SingleFileLimitBytes).ToArray();
        if (oversize.Length > 0)
        {
            return RelayQuotaDecision.Refuse(
                $"有 {oversize.Length} 个文件超过单文件上限 {Human(_quota.SingleFileLimitBytes)}" +
                $"（最大的一个 {Human(oversize.Max())}）", _quota);
        }

        var total = fileSizes.Sum();
        if (total > _quota.ShareTotalLimitBytes)
        {
            return RelayQuotaDecision.Refuse(
                $"本次合计 {Human(total)} 超过单次分享上限 {Human(_quota.ShareTotalLimitBytes)}", _quota);
        }

        if (storageLimitBytes > 0 && usedBytes + total > storageLimitBytes)
        {
            return RelayQuotaDecision.Refuse(
                $"账户已用 {Human(usedBytes)}，本次再传 {Human(total)} 会超过剩余的 " +
                $"{Human(Math.Max(0, storageLimitBytes - usedBytes))}", _quota);
        }

        return RelayQuotaDecision.Permit(
            $"在额度内：单文件与合计都未超过 {Human(_quota.ShareTotalLimitBytes)}", _quota);
    }

    public static string Human(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
