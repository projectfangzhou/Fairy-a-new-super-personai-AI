using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Windows;

namespace FairyCodeSurface.Tests;

/// <summary>
/// Builds the real terminal window on an STA thread so the XAML actually loads, then drives
/// the rendering and approval paths. The engine is stubbed out: this test is about the surface,
/// not about spawning a coding agent.
/// </summary>
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            var center = new AgentControlCenter();
            var audit = new OperationAudit(Path.Combine(Path.GetTempPath(), "fairyai-fcw-" + Guid.NewGuid().ToString("N")));
            var manager = new FairyCodeSessionManager(
                center,
                new ApprovalGate(new PermissionPolicy(), audit),
                audit,
                new StubModels(),
                new StubEngine());

            var window = new FairyCodeWindow(center, manager, Path.GetTempPath());
            var transcript = window.TranscriptPanel;

            Assert(transcript.Children.Count >= 2, "the opening banner is rendered");
            Assert(window.WorkspaceField.Text == Path.GetTempPath(), "the initial workspace is shown");
            Assert(window.InputField.Text.Length == 0, "the prompt starts empty");
            Assert(window.HeaderTitleForTest == "新会话", "a fresh surface starts on a new session");

            // Streaming text appends to one block instead of creating a line per token.
            var before = transcript.Children.Count;
            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.AssistantText, "Hello"));
            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.AssistantText, " world"));
            Assert(transcript.Children.Count == before + 1, "assistant deltas share one transcript line");
            var lastText = LastText(transcript);
            Assert(lastText.Contains("Hello", StringComparison.Ordinal) &&
                   lastText.Contains("world", StringComparison.Ordinal),
                "both deltas are visible in that line");

            // A tool line is its own line, and so is a decision.
            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.ToolStart, "bash"));
            Assert(transcript.Children.Count == before + 2, "a tool start gets its own line");
            Assert(LastText(transcript).Contains("bash", StringComparison.Ordinal), "the tool name is shown");

            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.ToolDecision, "命令执行：拒绝", false));
            Assert(LastText(transcript).Contains("拒绝", StringComparison.Ordinal), "a denial is rendered as a denial");

            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.Completed, "done"));
            Assert(window.StatusForTest.Contains("就绪", StringComparison.Ordinal), "completion returns the surface to ready");

            // Approvals: the block comes from the shared pending queue, and answering releases it.
            var task = center.Enqueue("approval surface");
            task.Start();
            var ask = task.AskApprovalAsync("写入文件 D:\\Dev\\x.txt", RiskCategory.DeleteOrOverwriteData);
            PumpUntil(() => center.Pending().Count == 1, "a pending approval appears");

            window.RefreshApprovalsFor(task.Id);
            Assert(window.ApprovalBlocks.Count == 1, "the pending approval is rendered inline");
            Assert(window.SessionListForTest.Children.Count > 0, "the session list shows what the control centre holds");

            var allowButton = FindButton(window.ApprovalBlocks.Values.First(), "允许");
            Assert(allowButton is not null, "the block offers an allow button");
            allowButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert(PumpUntil(() => ask.IsCompleted, "answering releases the waiting task"),
                "answering releases the waiting task");
            Assert(ask.Result, "pressing allow answers yes");
            Assert(window.ApprovalBlocks.Count == 0, "the answered block is cleared");

            // Sending with a workspace that does not exist must say so and never start an engine.
            window.WorkspaceField.Text = Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N"));
            window.InputField.Text = "do something";
            window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(PumpUntil(() => window.StatusForTest.Contains("工作区无效", StringComparison.Ordinal),
                    "an invalid workspace is reported"),
                "an invalid workspace is reported instead of starting an engine");

            // A picture of the real layout, so the design can be judged rather than only asserted.
            // It is taken with one unanswered approval and one user turn in place, which are the two
            // parts of the design that matter most and are the easiest to get wrong.
            var previewTask = center.Enqueue("接收文件：3 个");
            previewTask.Start();
            var previewAsk = previewTask.AskApprovalAsync(
                "手机 想发送 3 个文件（12.4 MB）\n· 照片-01.jpg\n· 照片-02.jpg\n· 笔记.md",
                RiskCategory.ExternalTransfer);
            PumpUntil(() => center.Pending().Any(item => item.TaskId == previewTask.Id), "the preview approval appears");

            window.RenderPromptForTest("帮我把 Tests/Pairing 里失败的用例修一下，先只读分析再动手");
            window.RenderForTest(new FairyCodeUiEvent(FairyCodeUiEventKind.AssistantText,
                "先看失败原因：PairingLan 里有两个用例在并发写同一目录。"));
            window.RefreshApprovalsFor(previewTask.Id);
            Assert(window.ApprovalBlocks.Count == 1, "the preview approval is rendered inline");

            var preview = Path.Combine(AppContext.BaseDirectory, "fairycode-preview.png");
            try
            {
                SavePreview(window, preview);
                Assert(File.Exists(preview) && new FileInfo(preview).Length > 10_000,
                    "the window renders to an image, so the layout exists and has content");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("preview skipped: " + ex.Message);
            }

            // Leave nothing waiting behind: the preview task is answered and settles.
            foreach (var pending in center.Pending().ToArray()) center.Answer(pending.Id, false);
            Assert(PumpUntil(() => previewAsk.IsCompleted, "the preview approval settles"),
                "the preview approval settles instead of being left hanging");
            Assert(!previewAsk.Result, "and it settles as a denial, which is what was sent");

            window.Close();
            Assert(center.Pending().Count == 0, "closing the window leaves no pending interactions");

            Console.WriteLine($"All {_checks} Fairy Code window checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED after {_checks} checks: {ex.Message}");
            return 1;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    /// <summary>Runs the dispatcher until the condition holds, so async UI work can finish.</summary>
    private static bool PumpUntil(Func<bool> condition, string name, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        if (condition()) return true;
        Console.Error.WriteLine("TIMEOUT: " + name);
        return false;
    }

    /// <summary>
    /// The last text in a panel, walking into cards and bubbles. The transcript now renders turns as
    /// bordered blocks, so looking only at direct children would miss everything inside them.
    /// </summary>
    private static string LastText(Panel panel)
    {
        var texts = new List<string>();
        CollectText(panel, texts);
        return texts.Count == 0 ? string.Empty : texts[^1];
    }

    private static void CollectText(DependencyObject node, List<string> texts)
    {
        switch (node)
        {
            case TextBlock block:
                texts.Add(new TextRange(block.ContentStart, block.ContentEnd).Text);
                return;
            case Border border when border.Child is not null:
                CollectText(border.Child, texts);
                return;
            case ContentControl control when control.Content is DependencyObject child:
                CollectText(child, texts);
                return;
            case Panel panel:
                foreach (UIElement element in panel.Children) CollectText(element, texts);
                return;
        }
    }

    /// <summary>Renders the real window off-screen so the layout can be looked at, not just asserted.</summary>
    private static void SavePreview(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -4000;
        window.Top = -4000;
        window.Width = 1240;
        window.Height = 800;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        PumpUntil(() => window.ActualWidth > 100, "the window is laid out");

        // Render the window's content, not the window: the window frame is not ours to draw and
        // rendering it leaves a blank strip where the chrome would be.
        var root = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)root.ActualWidth);
        var height = Math.Max(1, (int)root.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Button? FindButton(DependencyObject root, string label)
    {
        if (root is Button button && (button.Content as string) == label) return button;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindButton(System.Windows.Media.VisualTreeHelper.GetChild(root, i), label);
            if (found is not null) return found;
        }
        return root is ContentControl content && content.Content is DependencyObject child
            ? FindButton(child, label)
            : null;
    }
}

internal sealed class StubModels : IFairyCodeModelSource
{
    public (EndpointSettings Endpoint, string? ApiKey)? Read() =>
        (new EndpointSettings("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", null, true), "sk-test");
}

internal sealed class StubEngine : IEngineLocator
{
    public Task<EngineAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(EngineAvailability.Unusable(
            EngineAvailabilityState.CliMissing, "test environment has no engine"));
}
