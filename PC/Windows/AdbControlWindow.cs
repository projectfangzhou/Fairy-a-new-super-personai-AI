using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MyAiAssistant.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;

namespace MyAiAssistant.Windows;

public sealed class AdbControlWindow : Window
{
    private readonly TextBox _path = new();
    private readonly ComboBox _devices = new();
    private readonly ComboBox _key = new() { ItemsSource = AdbInputService.SupportedKeys };
    private readonly TextBox _x = new() { Text = "0" };
    private readonly TextBox _y = new() { Text = "0" };
    private readonly TextBox _x2 = new() { Text = "0" };
    private readonly TextBox _y2 = new() { Text = "0" };
    private readonly TextBox _duration = new() { Text = "300" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Margin = new Thickness(20) };
    private readonly CancellationTokenSource _closed = new();
    private AdbInputService? _service;

    public AdbControlWindow()
    {
        Title = "仙灵 · USB ADB 手机操控";
        Width = 580; Height = 750; MinWidth = 450; MinHeight = 500;
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _panel.Children.Add(new TextBlock
        {
            Text = "连接 USB，在手机上开启 USB 调试并确认此电脑的 RSA 授权。选择官方 platform-tools 的 adb.exe，刷新后显式选择手机。只有点击测试按钮才会发送动作。Shizuku 尚未集成。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        AddField("adb.exe 完整路径", _path);
        AddButton("浏览 adb.exe", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Android Debug Bridge|adb.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) _path.Text = picker.FileName;
            return Task.CompletedTask;
        });
        _path.TextChanged += (_, _) => { _service = null; _devices.ItemsSource = null; _status.Text = "路径已更改，请刷新设备。"; };
        AddButton("刷新已授权 USB 设备", async () =>
        {
            _service = null;
            _devices.ItemsSource = null;
            var service = new AdbInputService(_path.Text.Trim());
            var devices = await service.GetAuthorizedUsbDevicesAsync(_closed.Token);
            _service = service;
            _devices.ItemsSource = devices;
            _devices.SelectedIndex = -1;
            _status.Text = devices.Count == 0 ? "未发现已授权 USB 设备；检查连接与手机 RSA 授权。" : $"找到 {devices.Count} 台手机，请显式选择目标 serial。";
        });
        AddField("目标手机 serial（每次刷新后重新选择）", _devices);
        AddField("起点 X / 点击 X", _x); AddField("起点 Y / 点击 Y", _y);
        AddField("滑动终点 X", _x2); AddField("滑动终点 Y", _y2);
        AddField("滑动时长 ms（50–5000）", _duration);
        AddButton("在所选手机执行一次点击", () => ExecuteAsync((service, serial) => service.TapAsync(serial, Number(_x), Number(_y), _closed.Token)));
        AddButton("在所选手机执行一次滑动", () => ExecuteAsync((service, serial) => service.SwipeAsync(serial, Number(_x), Number(_y), Number(_x2), Number(_y2), Number(_duration), _closed.Token)));
        AddField("有限按键", _key);
        AddButton("在所选手机执行一次按键", () => ExecuteAsync((service, serial) => service.KeyAsync(serial, _key.SelectedItem as string ?? "", _closed.Token)));
        _status.Margin = new Thickness(0, 12, 0, 0);
        _panel.Children.Add(_status);
        Closed += (_, _) => _closed.Cancel();
    }

    private static int Number(TextBox input)
        => int.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("坐标及时间必须是非负整数。");

    private async Task ExecuteAsync(Func<AdbInputService, string, Task> operation)
    {
        if (_service == null || _devices.SelectedItem is not AdbDevice device)
            throw new InvalidOperationException("先刷新并显式选择一台已授权 USB 手机。");
        await operation(_service, device.Serial);
        _status.Text = $"ADB 已完成发送至 {device.Serial}；请在手机上核对实际效果。";
    }

    private void AddField(string label, Control control)
    {
        _panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        _panel.Children.Add(control);
    }

    private void AddButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) };
        button.Click += async (_, _) =>
        {
            _panel.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { _status.Text = "操作已取消或超时；若动作已发送，请在手机上核对。"; }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _panel.IsEnabled = true; }
        };
        _panel.Children.Add(button);
    }
}
