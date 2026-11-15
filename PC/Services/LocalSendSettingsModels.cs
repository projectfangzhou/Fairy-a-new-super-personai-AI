namespace MyAiAssistant.Services;

public sealed record LocalSendSettings(
    bool Enabled,
    bool Running,
    int Port,
    string Alias,
    string SaveDirectory,
    long MaxFileBytes,
    long MaxSessionBytes);

/// <summary>
/// The control-centre view of the LAN receiver. An interface so the dashboard can be tested
/// without touching the real configuration file or opening a listening socket.
/// </summary>
public interface ILocalSendSettings
{
    LocalSendSettings Read();

    (bool Ok, string Message) Apply(bool? enabled, string? alias, string? saveDirectory);
}
