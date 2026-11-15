using System.Windows;
using System.Windows.Controls;
using MyAiAssistant.Services;

namespace MyAiAssistant.Windows;

public sealed class DeepSeekHarnessWindow : Window
{
    private readonly TextBox _executable = new();
    private readonly TextBox _cliEntryPoint = new();
    private readonly TextBox _workspace = new();
    private readonly TextBox _prompt = new() { AcceptsReturn = true, MinHeight = 100, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _run = new() { Content = "确认并发送", Margin = new Thickness(0, 8, 8, 8) };
    private readonly Button _cancel = new() { Content = "取消", IsEnabled = false, Margin = new Thickness(0, 8, 0, 8) };
    private CancellationTokenSource? _running;

    public DeepSeekHarnessWindow()
    {
        Title = "DeepSeek Harness · 规划助手";
        Width = 760;
        Height = 700;
        MinWidth = 560;
        MinHeight = 500;
        var root = new DockPanel { Margin = new Thickness(18) };
        var form = new StackPanel();
        DockPanel.SetDock(form, Dock.Top);
        root.Children.Add(form);
        form.Children.Add(new TextBlock
        {
            Text = "复用仙灵当前 LLM 设置（OpenAI Chat Completions）。此入口禁用 Shell、文件和 MCP 工具，仅处理你提供的文本，不读取工作目录内容。每次运行是独立会话。需安装支持 sdk-minimal 和 llm-pi-ai 的运行时；可选 dsh.exe，或 node.exe 加已构建的 dsh lib/bin.js。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        AddPath(form, "dsh.exe 或 node.exe 路径", _executable, () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "运行时可执行程序|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) _executable.Text = dialog.FileName;
        });
        AddPath(form, "Node 模式：dsh lib/bin.js（使用 dsh.exe 时留空）", _cliEntryPoint, () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "已构建的 dsh 入口|bin.js", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) _cliEntryPoint.Text = dialog.FileName;
        });
        AddPath(form, "工作目录", _workspace, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择本次工作目录", Multiselect = false };
            if (dialog.ShowDialog(this) == true) _workspace.Text = dialog.FolderName;
        });
        form.Children.Add(new TextBlock { Text = "任务 / 待分析文本", Margin = new Thickness(0, 10, 0, 4) });
        form.Children.Add(_prompt);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_run);
        actions.Children.Add(_cancel);
        form.Children.Add(actions);
        root.Children.Add(_output);
        Content = root;
        _run.Click += Run;
        _cancel.Click += (_, _) => _running?.Cancel();
        Closed += (_, _) => _running?.Cancel();
    }

    private static void AddPath(Panel form, string label, TextBox field, Action browse)
    {
        form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 4) });
        var row = new DockPanel();
        var button = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => browse();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(field);
        form.Children.Add(row);
    }

    private async void Run(object sender, RoutedEventArgs args)
    {
        if (_running != null) return;
        using var cancellation = new CancellationTokenSource();
        _running = cancellation;
        _run.IsEnabled = false;
        _cancel.IsEnabled = true;
        _output.Text = "等待确认 / 运行中…";
        try
        {
            var entry = _cliEntryPoint.Text.Trim();
            var request = new HarnessRequest(_executable.Text.Trim(), _workspace.Text.Trim(), _prompt.Text,
                entry.Length == 0 ? null : entry);
            _output.Text = await new DeepSeekHarnessService().RunAsync(request, (consent, token) =>
            {
                token.ThrowIfCancellationRequested();
                var result = MessageBox.Show(this,
                    $"将启动：{consent.Executable}\ndsh 入口：{consent.CliEntryPoint ?? "内置"}\n工作目录：{consent.Workspace}\n模型端点：{consent.Endpoint}\n模型：{consent.Model}\n\n仅把输入文本发送给该模型服务。API Key 仅通过子进程环境传递。工具执行已禁用；不会自动安装依赖。运行时会临时记录会话，结束后尝试清理。\n\n允许本次发送？",
                    "本次运行确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                return Task.FromResult(result == MessageBoxResult.Yes);
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { _output.Text = "已取消，运行中的 dsh 子进程已停止。"; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or TimeoutException)
        {
            _output.Text = exception.Message;
        }
        catch (Exception) { _output.Text = "运行失败。请检查 dsh 安装、插件、目录权限和模型配置。为保护凭据，未显示原始进程诊断。"; }
        finally
        {
            _running = null;
            _run.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }
}
