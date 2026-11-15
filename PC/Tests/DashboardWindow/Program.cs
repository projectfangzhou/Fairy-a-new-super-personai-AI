using System.Windows;
using System.Windows.Controls;
using MyAiAssistant.Windows;

namespace DashboardWindow.Tests;

/// <summary>
/// Constructs the real window on an STA thread so the XAML is actually loaded and the
/// token widget is exercised, not merely compiled. The generated x:Name fields are
/// internal to the main assembly, so the controls are resolved by name at runtime.
/// </summary>
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            var url = "http://localhost:8080/";
            var token = "0123456789abcdef0123456789abcdef";
            var window = new DashboardAccessWindow(url, token);

            var urlText = Find<TextBlock>(window, "UrlText");
            var tokenText = Find<TextBlock>(window, "TokenText");
            var reveal = Find<Button>(window, "RevealButton");

            Assert(urlText.Text == url, "url is shown verbatim");
            Assert(tokenText.Text.Length == 32, "token is masked at full length");
            Assert(tokenText.Text.All(c => c == '•'), "token is not visible before revealing");
            Assert(!tokenText.Text.Contains(token[..8], StringComparison.Ordinal), "no plaintext leak in the mask");

            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(tokenText.Text == token, "reveal shows the token");
            Assert((string)reveal.Content == "隐藏令牌", "reveal toggles the label");

            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(tokenText.Text.All(c => c == '•'), "second click hides the token again");

            window.Close();
            Console.WriteLine($"All {_checks} dashboard window checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class
    {
        return root.FindName(name) as T ?? throw new Exception($"control {name} not found");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
}