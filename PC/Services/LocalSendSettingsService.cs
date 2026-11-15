using System.IO;
using MyAiAssistant.Services.LocalSend;

namespace MyAiAssistant.Services;


/// <summary>
/// Reads and applies the LAN receive settings. Turning the receiver on or off takes effect
/// immediately instead of waiting for a restart, and a failed bind is reported rather than
/// silently leaving the switch "on" with nothing listening.
/// </summary>
public sealed class LocalSendSettingsService : ILocalSendSettings
{
    private readonly LocalSendReceiveCoordinator _coordinator;
    private readonly LocalSendTransferService? _transfers;

    public LocalSendSettingsService(LocalSendReceiveCoordinator coordinator, LocalSendTransferService? transfers = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _transfers = transfers;
    }

    public LocalSendSettings Read()
    {
        var config = ConfigManager.Load().LocalSend;
        return new LocalSendSettings(
            config.Enabled,
            _coordinator.IsRunning,
            _coordinator.IsRunning ? _coordinator.Port : config.Port,
            _coordinator.Alias,
            _coordinator.SaveDirectory,
            config.MaxFileBytes,
            config.MaxSessionBytes);
    }

    public (bool Ok, string Message) Apply(bool? enabled, string? alias, string? saveDirectory)
    {
        var config = ConfigManager.Load();

        if (!string.IsNullOrWhiteSpace(alias))
        {
            var trimmed = alias.Trim();
            if (trimmed.Length > 40) return (false, "设备名过长（最多 40 个字符）");
            config.LocalSend.Alias = trimmed;
        }

        if (!string.IsNullOrWhiteSpace(saveDirectory))
        {
            try
            {
                var full = Path.GetFullPath(saveDirectory.Trim());
                Directory.CreateDirectory(full);
                config.LocalSend.SaveDirectory = full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return (false, $"接收目录不可用：{ex.GetType().Name}");
            }
        }

        if (enabled is not null) config.LocalSend.Enabled = enabled.Value;
        ConfigManager.Save(config);

        // Rebuild options so an alias or directory change applies to the live listener.
        _coordinator.UpdateOptions(LocalSendSettingsBinder.OptionsFromConfig());
        if (config.LocalSend.Enabled && !_coordinator.Start())
            return (false, $"已保存设置，但监听未能启动：{_coordinator.LastError ?? "端口被占用或权限不足"}");

        // Discovery follows the same switch: it exists to find peers to send to and to be found.
        if (config.LocalSend.Enabled) _transfers?.Start(LocalSendSettingsBinder.OptionsFromConfig());
        else _transfers?.Stop();

        return (true, config.LocalSend.Enabled ? "接收已开启" : "接收已关闭");
    }
}
