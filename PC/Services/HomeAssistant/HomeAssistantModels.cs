namespace MyAiAssistant.Services.HomeAssistant;

/// <summary>
/// A Home Assistant entity as the app needs it. Attributes are reduced to the two that the
/// control centre shows; the full attribute bag is not retained, because entity attributes can
/// carry location, presence and device identifiers.
/// </summary>
public sealed record HaEntity(
    string EntityId,
    string Domain,
    string FriendlyName,
    string State,
    string? Unit,
    DateTimeOffset? LastChanged)
{
    public bool IsScene => Domain == "scene";
    public bool IsAutomation => Domain == "automation";
}

/// <summary>
/// Tokens from /auth/token. <see cref="ToString"/> is redacted on purpose: these objects end up
/// in debugger views, logs and error messages, and a token printed once is a token leaked.
/// </summary>
public sealed record HaTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt.AddMinutes(-2);

    public override string ToString() =>
        $"HaTokenSet(access=***{AccessToken.Length}, refresh={(RefreshToken is null ? "无" : "***")}, expires={ExpiresAt:u})";
}

public sealed record HaServiceCall(string Domain, string Service, string? EntityId, string? DataJson)
{
    public string Target => EntityId ?? $"{Domain}.{Service}";
}

public sealed record HaApiInfo(string Message, string? LocationName, string? Version);

/// <summary>The server rejected the request: no token, or a token that is no longer valid.</summary>
public sealed class HaAuthenticationException : Exception
{
    public HaAuthenticationException(string message) : base(message) { }
}

/// <summary>The server answered, but not with what we asked for.</summary>
public sealed class HaRequestException : Exception
{
    public HaRequestException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
}

/// <summary>
/// Whether a service call needs the user's explicit yes, and under which risk category.
/// Reads never reach here; this only classifies writes.
/// </summary>
public static class HomeAssistantRisk
{
    /// <summary>
    /// Services that can restart the whole instance, run shell commands, or unlock a door.
    /// They are not blocked outright — the user may legitimately script `homeassistant.restart`
    /// — but they are labelled as privilege escalation so the audit log shows them apart from
    /// "turn the kitchen light on".
    /// </summary>
    private static readonly HashSet<string> PrivilegedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "homeassistant.restart",
        "homeassistant.stop",
        "homeassistant.reload_all",
        "shell_command",
        "hassio",
        "python_script",
        "lock.unlock",
        "mqtt.publish",
        "recorder.purge",
        "recorder.disable",
    };

    public static Permissions.RiskCategory Classify(HaServiceCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var qualified = $"{call.Domain}.{call.Service}";
        if (PrivilegedServices.Contains(qualified) || PrivilegedServices.Contains(call.Domain))
            return Permissions.RiskCategory.PrivilegeEscalation;

        return Permissions.RiskCategory.ControlExternalDevice;
    }

    public static string Describe(HaServiceCall call)
    {
        var target = string.IsNullOrWhiteSpace(call.EntityId) ? call.Domain : call.EntityId;
        return call.Domain switch
        {
            "scene" => $"执行场景 {target}",
            "script" => $"运行脚本 {target}",
            "automation" => $"触发自动化 {target}",
            "homeassistant" => $"调用 Home Assistant 系统服务 {call.Service}",
            _ => $"控制设备 {target}（{call.Domain}.{call.Service}）"
        };
    }
}
