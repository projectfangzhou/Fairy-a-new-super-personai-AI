namespace MyAiAssistant.Services.Dashboard;

/// <summary>当前 WebChat 服务状态。</summary>
public sealed record WebChatStatus(bool Running, int Port, string Url, int Clients);

/// <summary>
/// The control centre's handle on the built-in web chat server.
///
/// It exists so <see cref="DashboardApi"/> can start, stop and report the server without owning
/// it: the desktop app decides when it runs, and the dashboard only asks. Tests supply a fake or
/// leave it unset, and the API then reports the feature as unavailable rather than pretending.
/// </summary>
public interface IWebChatControl
{
    WebChatStatus Status();

    /// <summary>Starts the server (already running is not an error). Returns the message to show.</summary>
    (bool Ok, string Message) Start();

    (bool Ok, string Message) Stop();
}
