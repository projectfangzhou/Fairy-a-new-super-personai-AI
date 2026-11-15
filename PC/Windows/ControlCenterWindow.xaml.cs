using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace MyAiAssistant.Windows;

/// <summary>
/// The control centre as its own window, opened straight from the dock. The session token is
/// handed to the page once so the user is not asked to retype what this process already knows.
/// </summary>
public partial class ControlCenterWindow : Window
{
    private readonly string _url;
    private readonly string? _token;
    private WebView2? _webView;

    public ControlCenterWindow(string url, string? token)
    {
        _url = url;
        _token = token;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _webView = new WebView2();
            Host.Children.Add(_webView);
            await _webView.EnsureCoreWebView2Async();

            var injected = false;
            _webView.NavigationCompleted += async (_, args) =>
            {
                if (injected || !args.IsSuccess || string.IsNullOrEmpty(_token)) return;
                injected = true;
                try
                {
                    var script = $"sessionStorage.setItem('fairyToken', {JsonSerializer.Serialize(_token)}); location.reload();";
                    await _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
                catch (Exception) { /* the user can still paste the token by hand */ }
            };

            _webView.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            Host.Children.Clear();
            Host.Children.Add(BuildFallback(ex.GetType().Name));
        }
    }

    private UIElement BuildFallback(string reason)
    {
        var panel = new StackPanel { Margin = new Thickness(28), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(new TextBlock
        {
            Text = $"内嵌浏览器不可用（{reason}）",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC7, 0xD8)),
            FontSize = 14,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = _url,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x8C, 0xF5)),
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 10, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "可以在浏览器中打开上面的地址，并从托盘菜单「Agent 控制中心」复制会话令牌。",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x78, 0x90)),
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var open = new Button
        {
            Content = "在浏览器中打开",
            Width = 140,
            Height = 32,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开浏览器", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        panel.Children.Add(open);
        return panel;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _webView?.Dispose(); } catch (Exception) { }
        _webView = null;
        base.OnClosed(e);
    }
}