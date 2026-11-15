using MyAiAssistant.Services.Relay;

namespace MyAiAssistant.Services;

/// <summary>
/// Reads the relay limits from configuration. Kept apart from the pure policy so the policy can
/// be tested without a config file.
/// </summary>
public static class RelayQuotaBinding
{
    public static RelayQuotaPolicy FromConfiguration()
    {
        var config = ConfigManager.Load().Relay;

        // A zero or negative number is not "unlimited": it means the file was edited by hand and
        // broke, so fall back to the safe defaults instead of allowing everything.
        var single = config.SingleFileLimitBytes > 0 ? config.SingleFileLimitBytes : RelayQuota.FreeTier.SingleFileLimitBytes;
        var total = config.ShareTotalLimitBytes > 0 ? config.ShareTotalLimitBytes : RelayQuota.FreeTier.ShareTotalLimitBytes;

        var source = config.UserConfigured ? QuotaSource.UserConfigured : QuotaSource.FreeTierDefault;
        return new RelayQuotaPolicy(new RelayQuota(single, total, source));
    }

    public static long StorageBudgetBytes()
    {
        var budget = ConfigManager.Load().Relay.StorageBudgetBytes;
        return budget > 0 ? budget : RelayQuota.FreeTier.ShareTotalLimitBytes * 2;
    }
}
