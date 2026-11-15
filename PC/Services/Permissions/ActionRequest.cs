namespace MyAiAssistant.Services.Permissions;

/// <summary>
/// A single action the assistant wants to perform. <paramref name="Summary"/> and
/// <paramref name="Target"/> are shown to the user and written to the audit log, so
/// they must stay free of credentials and file contents; only parameter <em>names</em>
/// are retained, never their values.
/// </summary>
public sealed record ActionRequest(
    string ActionId,
    RiskCategory Category,
    string Summary,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    public bool IsRisky => Category != RiskCategory.None;
}

public enum PermissionVerdict
{
    Allow = 0,
    RequireApproval,
    Deny
}

public sealed record PolicyDecision(PermissionVerdict Verdict, string Reason);