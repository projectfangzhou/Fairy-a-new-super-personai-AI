using System.Windows;
using MyAiAssistant.Services;

namespace MyAiAssistant.Windows;

/// <summary>
/// 设备互联窗口：显示局域网服务状态、生成/展示配对码、列出已配对设备。
/// 配对码明文只在生成后短暂显示在窗口中，落盘的只有加盐哈希。
/// </summary>
public partial class DevicePairingWindow : Window
{
    private readonly ConnectivityManager _connectivity;

    public DevicePairingWindow(ConnectivityManager connectivity)
    {
        InitializeComponent();
        _connectivity = connectivity;
        RefreshStatus();
        RefreshPairedDevices();
    }

    private void RefreshStatus()
    {
        var config = ConfigManager.Load();
        if (_connectivity.Lan.IsRunning)
        {
            LanStatusText.Text = $"运行中，本机端点：{_connectivity.Lan.LocalEndpoint}";
            LanStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xB7));
        }
        else
        {
            LanStatusText.Text = "未运行（请检查 设置中的同步开关与局域网权限）";
            LanStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF8, 0x97, 0x97));
        }

        if (string.IsNullOrEmpty(config.Sync.PairingCode))
        {
            PairingCodeText.Text = "未设置";
            PairingHintText.Text = "点击「生成新配对码」，然后在手机端输入显示的 6 位数字";
        }
        else
        {
            PairingCodeText.Text = "●●●●●●";
            PairingHintText.Text = "已设置配对码（明文不保存，无法回看）。如遗忘请点击「生成新配对码」";
        }
    }

    private void RefreshPairedDevices()
    {
        var devices = LanSyncService.GetPairedDevices()
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}（{kv.Value.ToLocalTime():MM-dd HH:mm}）")
            .ToList();
        PairedDevicesList.ItemsSource = devices.Count > 0
            ? devices
            : new List<string> { "暂无已配对设备" };
    }

    private void OnGenerateCodeClick(object sender, RoutedEventArgs e)
    {
        var code = ConfigManager.GeneratePairingCode();
        var config = ConfigManager.Load();
        config.Sync.PairingCode = ConfigManager.HashPairingCode(code);
        ConfigManager.Save(config);

        PairingCodeText.Text = code;
        PairingHintText.Text = "请在手机端 设置 → 设备互联 中输入此配对码。关闭窗口后将无法回看。";
    }
}
