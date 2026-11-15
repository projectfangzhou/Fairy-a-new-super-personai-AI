using MyAiAssistant.Services.HomeAssistant;
using MyAiAssistant.Services.Permissions;

namespace MyAiAssistant.Services;

/// <summary>Settings the control centre may change. The token is not part of this.</summary>
public sealed record HomeAssistantSettings(string ServerUrl, string ClientId, string RedirectUri);

public sealed record HaEntityView(string EntityId, string Domain, string FriendlyName, string State, string? Unit);

public sealed record HaSceneView(string EntityId, string Name);

public sealed record HaAutomationView(string EntityId, string Name, string State);

/// <summary>
/// What the control centre shows. <see cref="EntityTotal"/> is the real count; the list itself
/// may be capped, and the page says so rather than pretending the house has 500 entities.
/// </summary>
public sealed record HomeAssistantView(
    bool Configured,
    bool Connected,
    string ServerUrl,
    string? LocationName,
    string? Version,
    string? Error,
    string? PendingAuthorizeUrl,
    string RedirectUri,
    DateTimeOffset? LastRefreshed,
    int EntityTotal,
    IReadOnlyList<HaEntityView> Entities,
    IReadOnlyList<HaSceneView> Scenes,
    IReadOnlyList<HaAutomationView> Automations);

public sealed record HomeAssistantActionResult(bool Ok, string Message, string? AuthorizeUrl = null);

/// <summary>
/// Asks the user before a service call runs. An interface so the control can be tested without
/// standing up the task queue, and so the app decides how the question is actually asked.
/// </summary>
public interface IHomeAssistantApprover
{
    Task<bool> AskAsync(ActionRequest request, CancellationToken cancellationToken);
}

public interface IHomeAssistantControl
{
    HomeAssistantView Read();

    HomeAssistantActionResult ApplySettings(string serverUrl, string? clientId, string? redirectUri);

    HomeAssistantActionResult BeginAuthorization();

    Task<HomeAssistantActionResult> CompleteAuthorizationAsync(string callback, CancellationToken cancellationToken = default);

    /// <summary>Fallback for instances where the OAuth flow is not usable.</summary>
    Task<HomeAssistantActionResult> UseAccessTokenAsync(string token, CancellationToken cancellationToken = default);

    Task<HomeAssistantActionResult> ConnectAsync(CancellationToken cancellationToken = default);

    HomeAssistantActionResult SignOut();

    Task<HomeAssistantActionResult> CallServiceAsync(string domain, string service, string? entityId,
        string? dataJson, CancellationToken cancellationToken = default);
}
