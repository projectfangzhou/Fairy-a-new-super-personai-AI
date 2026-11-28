using MyAiAssistant.Services.Dashboard;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The launch rule for the built-in web chat server, kept out of the WPF <c>App</c> class so it can
/// be exercised without starting the desktop app.
///
/// The rule itself is the security decision: the server answers with the user's model and key and
/// listens on a loopback port any local process can reach, so it must only come up when the user
/// deliberately enabled a WebChat account.
/// </summary>
public static class WebChatLaunch
{
    public static bool ShouldStart(IEnumerable<MessagingAccountView> accounts) =>
        accounts.Any(account => account.Platform == PlatformKind.WebChat &&
                                account.Enabled &&
                                account.Implemented);

    /// <summary>
    /// Starts the server when the rule says so. Returns what to log; never throws — a surface that
    /// cannot start must not take the app down with it.
    /// </summary>
    public static (bool Started, string Message) StartIfEnabled(
        MessagingAccountStore store, IWebChatControl control)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(control);

        if (!ShouldStart(store.List()))
            return (false, "没有启用的 WebChat 账号，服务不启动，端口保持关闭。");

        return control.Start();
    }
}
