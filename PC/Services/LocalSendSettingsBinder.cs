using System.IO;
using MyAiAssistant.Services.LocalSend;

namespace MyAiAssistant.Services;

/// <summary>
/// Turns the persisted LocalSend settings into the options the receiver needs. Kept separate from
/// the coordinator so the coordinator itself has no file-system or configuration dependency.
/// </summary>
public static class LocalSendSettingsBinder
{
    public static LocalSendReceiveOptions OptionsFromConfig()
    {
        var config = ConfigManager.Load().LocalSend;

        var saveDirectory = string.IsNullOrWhiteSpace(config.SaveDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FairyAI", "接收")
            : config.SaveDirectory;

        return new LocalSendReceiveOptions(
            string.IsNullOrWhiteSpace(config.Alias) ? "仙灵" : config.Alias,
            "Windows", "desktop",
            string.IsNullOrWhiteSpace(config.Fingerprint)
                ? LocalSendProtocol.CreateFingerprint()
                : config.Fingerprint,
            config.Port <= 0 ? LocalSendProtocol.DefaultPort : config.Port,
            saveDirectory)
        {
            MaxFileBytes = config.MaxFileBytes > 0 ? config.MaxFileBytes : 2L * 1024 * 1024 * 1024,
            MaxSessionBytes = config.MaxSessionBytes > 0 ? config.MaxSessionBytes : 5L * 1024 * 1024 * 1024
        };
    }
}
