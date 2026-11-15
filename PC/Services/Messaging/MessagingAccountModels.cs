namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// One credential a platform needs. Platforms differ: Telegram takes a single bot token, the QQ
/// official API takes an AppID plus an AppSecret. Declaring them lets the UI build the right form
/// instead of forcing every platform into one box.
/// </summary>
public sealed record PlatformCredentialField(string Key, string Label, bool IsSecret);

/// <summary>
/// One configured account. Every credential value is stored encrypted and never handed back to a
/// UI; views only report which fields are filled.
/// </summary>
public sealed record MessagingAccount(
    string Id,
    PlatformKind Platform,
    string DisplayName,
    bool Enabled,
    Dictionary<string, string>? CredentialCiphers = null,
    /// <summary>Legacy single-token field kept so older config files still load.</summary>
    string? TokenCipher = null);

/// <summary>What a UI is allowed to see about an account.</summary>
public sealed record MessagingAccountView(
    string Id,
    PlatformKind Platform,
    string PlatformName,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> FilledFields,
    IReadOnlyList<string> MissingFields,
    bool Implemented)
{
    public bool HasAllCredentials => MissingFields.Count == 0;
}