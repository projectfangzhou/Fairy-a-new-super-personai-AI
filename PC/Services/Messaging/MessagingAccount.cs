using System.Net.Http;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// Keeps the account list in the app config. Multiple accounts per platform are allowed on
/// purpose: one bot per group of chats is a normal setup, and the install wizard should not have
/// to guess how many the user needs.
/// </summary>
public sealed class MessagingAccountStore
{
    private readonly Func<AppConfig> _load;
    private readonly Action<AppConfig> _save;

    public MessagingAccountStore(Func<AppConfig>? load = null, Action<AppConfig>? save = null)
    {
        _load = load ?? ConfigManager.Load;
        _save = save ?? ConfigManager.Save;
    }

    public IReadOnlyList<MessagingAccountView> List()
    {
        var config = _load();
        return config.MessagingAccounts.Select(ToView).ToArray();
    }

    public MessagingAccount? Find(string id) =>
        _load().MessagingAccounts.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Adds an account. Only platforms with a real adapter can be added — configuring a platform
    /// that cannot be probed would just create a row that never works.
    /// </summary>
    public (bool Ok, string Message, string? Id) Add(
        PlatformKind platform, string displayName, IReadOnlyDictionary<string, string>? credentials)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return (false, "请填写账号名称。", null);

        if (!MessagingAccountService.IsImplemented(platform))
            return (false, $"{platform} 尚未实现，暂时不能添加。接入前不会显示为可用。", null);

        var missing = MessagingAccountService.RequiredFields(platform)
            .Where(f => !CredentialValues.Has(credentials, f.Key))
            .ToArray();
        if (missing.Length > 0)
            return (false, "还需要填写：" + string.Join("、", missing.Select(f => f.Label)), null);

        var config = _load();
        var trimmedName = displayName.Trim();
        if (config.MessagingAccounts.Any(a => a.Platform == platform &&
                                              string.Equals(a.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return (false, "同名账号已存在。", null);

        var ciphers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in MessagingAccountService.RequiredFields(platform))
            ciphers[field.Key] = ApiKeyProtector.Protect(credentials![field.Key].Trim());

        var id = Guid.NewGuid().ToString("N")[..12];
        config.MessagingAccounts.Add(new MessagingAccount(id, platform, trimmedName, true, ciphers));
        _save(config);
        return (true, "已添加。", id);
    }

    public bool Remove(string id)
    {
        var config = _load();
        var removed = config.MessagingAccounts.RemoveAll(a => a.Id == id) > 0;
        if (removed) _save(config);
        return removed;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        var config = _load();
        var index = config.MessagingAccounts.FindIndex(a => a.Id == id);
        if (index < 0) return false;
        config.MessagingAccounts[index] = config.MessagingAccounts[index] with { Enabled = enabled };
        _save(config);
        return true;
    }

    /// <summary>Decrypts the stored credentials for one account. Never expose this to a UI.</summary>
    public Dictionary<string, string> ReadCredentials(MessagingAccount account)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (account.CredentialCiphers is not null)
            foreach (var (key, cipher) in account.CredentialCiphers)
                if (!string.IsNullOrEmpty(cipher))
                    result[key] = ApiKeyProtector.Unprotect(cipher);

        // Legacy single-token record.
        if (result.Count == 0 && !string.IsNullOrEmpty(account.TokenCipher))
            result["token"] = ApiKeyProtector.Unprotect(account.TokenCipher);

        return result;
    }

    private static MessagingAccountView ToView(MessagingAccount account)
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (account.CredentialCiphers is not null)
            foreach (var key in account.CredentialCiphers.Keys) stored[key] = "1";
        if (stored.Count == 0 && !string.IsNullOrEmpty(account.TokenCipher)) stored["token"] = "1";

        var required = MessagingAccountService.RequiredFields(account.Platform);
        var filled = required.Where(f => stored.ContainsKey(f.Key)).Select(f => f.Label).ToArray();
        var missing = required.Where(f => !stored.ContainsKey(f.Key)).Select(f => f.Label).ToArray();

        return new MessagingAccountView(
            account.Id,
            account.Platform,
            account.Platform.ToString(),
            account.DisplayName,
            account.Enabled,
            filled,
            missing,
            MessagingAccountService.IsImplemented(account.Platform));
    }
}

/// <summary>Builds a probe for one account. Unimplemented platforms report Unavailable.</summary>
public sealed class MessagingAccountService
{
    private readonly MessagingAccountStore _store;
    private readonly Func<string, HttpClient>? _httpFactory;
    private readonly QQOfficialClient? _qqOfficialClient;

    public MessagingAccountService(MessagingAccountStore? store = null, Func<string, HttpClient>? httpFactory = null,
        QQOfficialClient? qqOfficialClient = null)
    {
        _store = store ?? new MessagingAccountStore();
        _httpFactory = httpFactory;
        _qqOfficialClient = qqOfficialClient;
    }

    public static bool IsImplemented(PlatformKind platform) =>
        platform is PlatformKind.Telegram or PlatformKind.QQ or PlatformKind.QQOfficial;

    /// <summary>
    /// Credentials each platform needs. QQ through the OpenClaw plugin authenticates in its own
    /// terminal (QR binding), so nothing is collected here; the QQ official API instead needs an
    /// AppID and AppSecret, which is why that route is declared with two fields.
    /// </summary>
    public static IReadOnlyList<PlatformCredentialField> RequiredFields(PlatformKind platform) => platform switch
    {
        PlatformKind.Telegram => [new("token", "Bot Token", true)],
        PlatformKind.QQ => [],
        PlatformKind.QQOfficial => [new("appId", "AppID", false), new("appSecret", "AppSecret", true)],
        PlatformKind.Discord => [new("botToken", "Bot Token", true)],
        PlatformKind.Feishu => [new("appId", "AppID", false), new("appSecret", "AppSecret", true)],
        PlatformKind.DingTalk => [new("clientId", "ClientId", false), new("clientSecret", "ClientSecret", true)],
        PlatformKind.Slack => [new("botToken", "Bot Token (xoxb)", true)],
        PlatformKind.WeCom => [new("corpId", "CorpID", false), new("corpSecret", "CorpSecret", true)],
        _ => []
    };

    public async Task<PlatformProbeResult> ProbeAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = _store.Find(accountId);
        if (account is null) return new(PlatformProbeState.Failed, "账号不存在。");

        if (!IsImplemented(account.Platform))
            return new(PlatformProbeState.Unavailable, $"{account.Platform} 尚未实现，暂时无法连接。");

        var credentials = _store.ReadCredentials(account);

        return account.Platform switch
        {
            PlatformKind.Telegram => await new TelegramAdapter(
                    CredentialValues.Get(credentials, "token"),
                    _httpFactory?.Invoke(CredentialValues.Get(credentials, "token") ?? ""))
                .ProbeAsync(cancellationToken).ConfigureAwait(false),

            PlatformKind.QQ => await new QQAdapter().ProbeAsync(cancellationToken).ConfigureAwait(false),

            PlatformKind.QQOfficial => await new QQOfficialAdapter(
                    () => CredentialValues.Get(credentials, "appId"),
                    () => CredentialValues.Get(credentials, "appSecret"),
                    _qqOfficialClient)
                .ProbeAsync(cancellationToken).ConfigureAwait(false),

            _ => new(PlatformProbeState.Unavailable, $"{account.Platform} 尚未实现。")
        };
    }
}


internal static class CredentialValues
{
    public static bool Has(IReadOnlyDictionary<string, string>? source, string key) =>
        source is not null && source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    public static string? Get(IReadOnlyDictionary<string, string>? source, string key) =>
        Has(source, key) ? source![key] : null;
}
