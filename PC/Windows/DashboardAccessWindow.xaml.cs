using System.Diagnostics;
using System.Windows;

namespace MyAiAssistant.Windows;

/// <summary>
/// Shows the loopback control-centre address and the per-session control token.
/// The token is intentionally only visible here: it must not be written to disk, or a
/// process running as the same user — including the assistant's own tooling — could read
/// it and approve a high-risk action on the user's behalf.
/// </summary>
public partial class DashboardAccessWindow : Window
{
    private readonly string _url;
    private readonly string _token;
    private bool _revealed;

    public DashboardAccessWindow(string url, string token)
    {
        InitializeComponent();
        _url = url;
        _token = token;
        UrlText.Text = url;
        TokenText.Text = Mask();
    }

    private string Mask() => new('•', Math.Min(_token.Length, 32));

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        TokenText.Text = _revealed ? _token : Mask();
        RevealButton.Content = _revealed ? "隐藏令牌" : "显示令牌";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_token);
            CopyButton.Content = "已复制";
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; say so instead of pretending.
            CopyButton.Content = "复制失败";
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开浏览器：{ex.Message}", "Agent 控制中心",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}