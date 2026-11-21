using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Theme;

namespace MyAiAssistant.Windows;

/// <summary>
/// The Fairy Code surface: sessions on the left, the conversation in the middle, and what the
/// session is attached to on the right.
///
/// The engine is still a command-line process and its output is still text — the difference from a
/// plain terminal is that a tool call is now a block you can read at a glance and answer on the
/// spot, and that the session list makes it obvious which conversation you are looking at. Every
/// approval here is the same pending item the control centre and HUD show, so answering in either
/// place is answering once.
/// </summary>
public partial class FairyCodeWindow : Window
{
    // These brushes are read from the active theme rather than hard-coded, so the window
    // follows a theme switch instead of staying on the old palette.
    private static Brush AssistantBrush => ThemeManager.GetBrush("TextPrimaryBrush", Paint("#E6E6EF"));
    private static Brush UserBrush => ThemeManager.GetBrush("TextPrimaryBrush", Paint("#EDEDF3"));
    private static Brush AccentBrush => ThemeManager.GetBrush("AccentBrush", Paint("#B3A6F7"));
    private static Brush WarnBrush => ThemeManager.GetBrush("ErrorBrush", Paint("#FF9D94"));
    private static Brush OkBrush => ThemeManager.GetBrush("SuccessBrush", Paint("#86E0A8"));
    private static Brush DimBrush => ThemeManager.GetBrush("TextMutedBrush", Paint("#8A8A96"));
    private static Brush ErrorBrush => ThemeManager.GetBrush("ErrorBrush", Paint("#FF7B72"));

    /// <summary>
    /// A brush from the active theme, falling back to the old hard-coded value when the
    /// window is built before the theme dictionary is in place.
    /// </summary>
    private static Brush Themed(string key, string fallback) =>
        ThemeManager.GetBrush(key, Paint(fallback));


    private readonly AgentControlCenter _center;
    private readonly FairyCodeSessionManager _manager;
    private readonly Dictionary<string, Border> _approvalBlocks = new(StringComparer.Ordinal);
    private readonly List<(string Text, bool? Allowed)> _tools = [];

    private FairyCodeSession? _session;
    private TextBlock? _openAssistantLine;
    private Border? _openAssistantCard;
    private bool _busy;
    private bool _closed;

    public FairyCodeWindow(AgentControlCenter center, FairyCodeSessionManager manager, string? initialWorkspace = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        InitializeComponent();

        WorkspaceBox.Text = initialWorkspace ?? DefaultWorkspace();
        InputBox.KeyDown += OnInputKeyDown;
        SendButton.Click += (_, _) => _ = SendAsync();
        StopButton.Click += (_, _) => StopSession();
        ClearButton.Click += (_, _) => ClearTranscript();
        NewTaskButton.Click += (_, _) => StartNewTask();
        PendingNavButton.Click += (_, _) => JumpToFirstApproval();
        _center.Changed += OnCenterChanged;
        Closed += OnClosed;

        AppendSystem("Fairy Code · Pi Agent 主循环");
        AppendSystem("确认工作区后直接输入指令。每一次工具调用都要先经过审批：覆盖文件、执行命令、读取凭据都会在这里问你是否允许。");
        SetStatus("待命：输入第一条指令即可启动引擎");
        RefreshChrome();
    }

    /// <summary>Where the transcript lives, exposed for the window tests.</summary>
    internal StackPanel TranscriptPanel => TranscriptHost;

    internal IReadOnlyDictionary<string, Border> ApprovalBlocks => _approvalBlocks;

    internal TextBox InputField => InputBox;

    internal TextBox WorkspaceField => WorkspaceBox;

    /// <summary>Used by the window tests: same rendering path, no engine.</summary>
    internal void RenderForTest(FairyCodeUiEvent uiEvent) => RenderUiEvent(uiEvent);

    internal string StatusForTest => StatusText.Text;

    internal StackPanel SessionListForTest => SessionList;

    internal string HeaderTitleForTest => HeaderTitle.Text;

    /// <summary>Renders a user turn the same way the composer does, for the layout preview.</summary>
    internal void RenderPromptForTest(string text) => AppendUser(text);

    private string DefaultWorkspace()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
    }

    // ---- actions ----

    private async Task SendAsync()
    {
        if (_busy) return;
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        var workspace = WorkspaceBox.Text.Trim();
        if (!Directory.Exists(workspace))
        {
            AppendError($"工作区不存在：{workspace}");
            SetStatus("工作区无效，未启动引擎");
            return;
        }

        _busy = true;
        InputBox.Clear();
        try
        {
            if (_session is null)
            {
                SetStatus("正在启动 Pi Agent…");
                AppendSystem($"启动引擎（工作区 {workspace}）");
                var (result, session) = await _manager.OpenAsync(
                    workspace, RenderFromAnyThread, $"Fairy Code · {Shorten(text, 40)}");
                if (!result.Ok || session is null)
                {
                    AppendError(result.Message);
                    SetStatus("启动失败：" + result.Message);
                    return;
                }

                _session = session;
                StopButton.IsEnabled = true;
                HeaderTitle.Text = $"Fairy Code · {Shorten(text, 40)}";
                AppendSystem($"会话 {session.TaskId} · 模型 {session.Model ?? "未知"}");
                SetStatus($"运行中 · {session.TaskId}");
            }

            AppendUser(text);
            OpenAssistantLine();
            await _session.SendPromptAsync(text);
            SetStatus($"运行中 · {_session.TaskId}");
        }
        catch (Exception ex)
        {
            AppendError($"发送失败：{ex.Message}");
            SetStatus("发送失败");
        }
        finally
        {
            _busy = false;
            RefreshApprovals();
            RefreshChrome();
        }
    }

    private void StartNewTask()
    {
        StopSession();
        ClearTranscript();
        _session = null;
        StopButton.IsEnabled = false;
        HeaderTitle.Text = "新会话";
        AppendSystem("新任务：输入指令后会启动一个新的引擎会话");
        SetStatus("待命：新任务");
        RefreshChrome();
    }

    private void JumpToFirstApproval()
    {
        var first = _approvalBlocks.Values.FirstOrDefault();
        if (first is null)
        {
            SetStatus("当前没有待确认的动作");
            return;
        }

        first.BringIntoView();
        SetStatus("已跳到待确认项");
    }

    private void StopSession()
    {
        if (_session is null) return;
        try { _manager.Stop(_session.TaskId); }
        catch (Exception ex) { AppendError($"停止失败：{ex.Message}"); }
        AppendSystem("已请求停止：当前原子操作结束后不再执行任何工具调用");
        StopButton.IsEnabled = false;
        SetStatus("已停止");
        RefreshChrome();
    }

    private void ClearTranscript()
    {
        TranscriptHost.Children.Clear();
        _openAssistantLine = null;
        _openAssistantCard = null;
        _approvalBlocks.Clear();
        RefreshChrome();
    }

    // ---- rendering ----

    private void RenderFromAnyThread(FairyCodeUiEvent uiEvent)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(new Action(() => RenderUiEvent(uiEvent)));
    }

    private void RenderUiEvent(FairyCodeUiEvent uiEvent)
    {
        if (_closed) return;
        switch (uiEvent.Kind)
        {
            case FairyCodeUiEventKind.AssistantText:
                AppendAssistant(uiEvent.Text);
                break;
            case FairyCodeUiEventKind.ToolStart:
                AppendTool(uiEvent.Text, null);
                break;
            case FairyCodeUiEventKind.ToolDecision:
                AppendTool(uiEvent.Text, uiEvent.Allowed);
                break;
            case FairyCodeUiEventKind.Completed:
                AppendSystem("本轮完成，可以继续输入下一条指令");
                SetStatus("就绪");
                break;
            case FairyCodeUiEventKind.Failed:
                AppendError(uiEvent.Text);
                StopButton.IsEnabled = false;
                SetStatus("引擎报错");
                break;
            case FairyCodeUiEventKind.Stopped:
                AppendSystem(uiEvent.Text);
                break;
            case FairyCodeUiEventKind.PromptSent:
            case FairyCodeUiEventKind.Opened:
                break;
        }

        RefreshChrome();
    }

    /// <summary>The user's turn: a right-aligned bubble, the one place alignment carries meaning.</summary>
    private void AppendUser(string text)
    {
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = UserBrush
        };
        body.Inlines.Add(new Run(text));

        var bubble = new Border
        {
            Background = Themed("SurfaceRaisedBrush", "#1E1E25"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(72, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 560,
            Child = body
        };

        TranscriptHost.Children.Add(bubble);
        _openAssistantLine = null;
        _openAssistantCard = null;
        ScrollToEnd();
    }

    private void OpenAssistantLine()
    {
        var label = new TextBlock
        {
            Text = "仙灵",
            FontSize = 10.5,
            Foreground = AccentBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = AssistantBrush
        };

        var content = new StackPanel();
        content.Children.Add(label);
        content.Children.Add(body);

        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 4, 40, 4),
            Child = content
        };

        TranscriptHost.Children.Add(card);
        _openAssistantCard = card;
        _openAssistantLine = body;
        ScrollToEnd();
    }

    private void AppendAssistant(string text)
    {
        if (_openAssistantLine is null) OpenAssistantLine();
        _openAssistantLine!.Inlines.Add(new Run(text));
        ScrollToEnd();
    }

    /// <summary>
    /// A tool call as its own block: the name and what it acts on are what the reader needs, so the
    /// path-like part is set in the mono face and the verdict is carried by colour and a mark.
    /// </summary>
    private void AppendTool(string text, bool? allowed)
    {
        _tools.Add((text, allowed));

        var mark = allowed switch
        {
            true => "✓",
            false => "✕",
            _ => "●"
        };
        var markBrush = allowed switch
        {
            true => OkBrush,
            false => WarnBrush,
            _ => AccentBrush
        };

        var header = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas")
        };
        header.Inlines.Add(new Run(mark + "  ") { Foreground = markBrush, FontWeight = FontWeights.SemiBold });
        header.Inlines.Add(new Run(text) { Foreground = AssistantBrush });

        var card = new Border
        {
            Background = Themed("SurfaceSunkenBrush", "#121217"),
            BorderBrush = Themed("BorderStrongBrush", "#1F1F26"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 5, 40, 5),
            Child = header
        };

        TranscriptHost.Children.Add(card);
        _openAssistantLine = null;
        _openAssistantCard = null;
        ScrollToEnd();
    }

    private void AppendSystem(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = DimBrush,
            Margin = new Thickness(0, 5, 0, 5)
        };

        TranscriptHost.Children.Add(block);
        _openAssistantLine = null;
        _openAssistantCard = null;
        ScrollToEnd();
    }

    private void AppendError(string text)
    {
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = ErrorBrush
        };
        body.Inlines.Add(new Run("!  ") { FontWeight = FontWeights.SemiBold });
        body.Inlines.Add(new Run(text));

        var card = new Border
        {
            Background = Themed("ErrorSurfaceBrush", "#1E1416"),
            BorderBrush = Themed("ErrorBrush", "#4A2A2C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 5, 40, 5),
            Child = body
        };

        TranscriptHost.Children.Add(card);
        _openAssistantLine = null;
        _openAssistantCard = null;
        ScrollToEnd();
    }

    private void ScrollToEnd() => Scroll.ScrollToEnd();

    // ---- approvals ----

    private void OnCenterChanged(AgentControlEvent controlEvent)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshApprovals();
            RefreshChrome();
        }));
    }

    internal void RefreshApprovals() => RefreshApprovalsFor(_session?.TaskId);

    /// <summary>
    /// Renders the pending queue for one task. The queue itself lives in the control centre,
    /// so an approval answered on the dashboard also clears the block here — and the gate is
    /// released by whoever answered first.
    /// </summary>
    internal void RefreshApprovalsFor(string? taskId)
    {
        if (_closed) return;
        var items = taskId is null
            ? Array.Empty<PendingInteractionSnapshot>()
            : _center.Pending().Where(p => p.TaskId == taskId).ToArray();

        foreach (var id in _approvalBlocks.Keys.ToArray())
        {
            if (items.Any(item => item.Id == id)) continue;
            MarkResolved(_approvalBlocks[id]);
            _approvalBlocks.Remove(id);
        }

        foreach (var item in items.Where(item => !_approvalBlocks.ContainsKey(item.Id)))
            AddApprovalBlock(item);
    }

    private void AddApprovalBlock(PendingInteractionSnapshot item)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = AssistantBrush,
            FontSize = 12.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas")
        };
        text.Inlines.Add(new Run("需要确认") { Foreground = WarnBrush, FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run(item.Risk is null ? "  " : $"  ·  {item.Risk.Value.Describe()}  ")
        {
            Foreground = WarnBrush
        });
        text.Inlines.Add(new Run(Environment.NewLine));

        // The prompt may carry a change preview; colour the +/- lines so the diff reads as a
        // diff instead of a wall of text.
        var lines = item.Prompt.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var color = line.StartsWith('+') ? OkBrush
                : line.StartsWith('-') ? WarnBrush
                : line.StartsWith("@@", StringComparison.Ordinal) ? AccentBrush
                : AssistantBrush;
            text.Inlines.Add(new Run(line + (i < lines.Length - 1 ? Environment.NewLine : string.Empty))
            {
                Foreground = color
            });
        }

        var allow = new Button
        {
            Content = "允许", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 9, 6, 0),
            Tag = item.Id, Background = Themed("SuccessBrush", "#1F2A22"), BorderBrush = Themed("SuccessBrush", "#2E4636"), Foreground = OkBrush
        };
        allow.Click += (_, _) => Answer(item.Id, true);

        var deny = new Button
        {
            Content = "拒绝", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 9, 0, 0),
            Tag = item.Id
        };
        deny.Click += (_, _) => Answer(item.Id, false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);

        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(buttons);

        var border = new Border
        {
            Background = Themed("ErrorSurfaceBrush", "#191317"),
            BorderBrush = Themed("ErrorBrush", "#4A3030"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 40, 6),
            Child = content
        };

        TranscriptHost.Children.Add(border);
        _approvalBlocks[item.Id] = border;
        _openAssistantLine = null;
        _openAssistantCard = null;
        ScrollToEnd();
        SetStatus($"等待你确认：{item.Id}");
        RefreshChrome();
    }

    private void Answer(string interactionId, bool approved)
    {
        var answered = _center.Answer(interactionId, approved);
        if (!answered) AppendSystem($"该待确认项已不存在：{interactionId}");
        RefreshApprovals();
        RefreshChrome();
    }

    private void MarkResolved(Border border)
    {
        if (border.Child is not StackPanel content) return;
        foreach (var button in content.Children.OfType<StackPanel>()
                     .SelectMany(panel => panel.Children.OfType<Button>()))
        {
            button.IsEnabled = false;
            button.Content = "已处理";
        }
        border.BorderBrush = Themed("BorderStrongBrush", "#2A2A33");
        border.Background = Themed("SurfaceSunkenBrush", "#141418");
        SetStatus("已处理");
    }

    // ---- side panels ----

    /// <summary>
    /// Rebuilds the sidebar list, the pending summary and the tool list from what the control
    /// centre actually holds. Nothing here is remembered separately, so the window cannot drift
    /// from the truth the other surfaces show.
    /// </summary>
    private void RefreshChrome()
    {
        if (_closed) return;

        var tasks = _center.Tasks().OrderByDescending(task => task.CreatedAt).Take(40).ToArray();
        SessionCount.Text = tasks.Length.ToString();
        SessionList.Children.Clear();

        string? currentDay = null;
        foreach (var task in tasks)
        {
            var day = DayLabel(task.CreatedAt);
            if (day != currentDay)
            {
                currentDay = day;
                SessionList.Children.Add(new TextBlock
                {
                    Text = day,
                    Foreground = Themed("TextMutedBrush", "#5C5C68"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(8, 10, 0, 4)
                });
            }

            SessionList.Children.Add(BuildSessionRow(task));
        }

        var pending = _session is null
            ? Array.Empty<PendingInteractionSnapshot>()
            : _center.Pending().Where(item => item.TaskId == _session.TaskId).ToArray();

        PendingNavCount.Text = pending.Length.ToString();
        PendingBadge.Visibility = pending.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingPanelCount.Text = pending.Length.ToString();

        PendingList.Children.Clear();
        foreach (var item in pending)
        {
            var row = new Border
            {
                Background = Themed("ErrorSurfaceBrush", "#191317"),
                BorderBrush = Themed("ErrorBrush", "#4A3030"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = FirstLine(item.Prompt),
                    Foreground = WarnBrush,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 44
                }
            };
            PendingList.Children.Add(row);
        }
        PendingEmpty.Visibility = pending.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        ToolCount.Text = _tools.Count.ToString();
        ToolList.Children.Clear();
        foreach (var (text, allowed) in _tools.TakeLast(8).Reverse())
        {
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            line.Inlines.Add(new Run(allowed switch { true => "✓ ", false => "✕ ", _ => "● " })
            {
                Foreground = allowed switch { true => OkBrush, false => WarnBrush, _ => AccentBrush }
            });
            line.Inlines.Add(new Run(FirstLine(text)) { Foreground = Themed("TextSecondaryBrush", "#A9A9B8") });
            ToolList.Children.Add(line);
        }
        ToolEmpty.Visibility = _tools.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var snapshot = _session is null ? null : _center.Task(_session.TaskId);
        PanelTaskId.Text = _session?.TaskId ?? "—";
        PanelState.Text = snapshot?.State.ToString() ?? "待命";
        PanelModel.Text = _session is null ? "未启动" : $"{_session.Engine ?? "pi"} · {_session.Model ?? "未知"}";
        ModelPill.Text = _session is null ? "引擎未检测" : _session.Model ?? "模型未知";
        SidebarModel.Text = _session is null ? "引擎未启动" : $"{_session.TaskId} · {_session.Model ?? "未知"}";
        HeaderState.Text = snapshot?.State.ToString() ?? "待命";
    }

    private Border BuildSessionRow(AgentTaskSnapshot task)
    {
        var isCurrent = _session is not null && task.Id == _session.TaskId;

        var title = new TextBlock
        {
            Text = task.Title,
            Foreground = isCurrent ? Themed("TextPrimaryBrush", "#F1F1F6") : Themed("TextSecondaryBrush", "#C9C9D2"),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var meta = new TextBlock
        {
            Text = $"{task.Id} · {task.State}",
            Foreground = Themed("TextMutedBrush", "#5C5C68"),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(meta);

        return new Border
        {
            Background = isCurrent ? Themed("SurfaceRaisedBrush", "#191920") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Child = content
        };
    }

    private static string DayLabel(DateTimeOffset createdAtUtc)
    {
        var local = createdAtUtc.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;

        if (local == today) return "今天";
        if (local == today.AddDays(-1)) return "昨天";
        return local.ToString("M月d日");
    }

    private static string FirstLine(string value)
    {
        var line = value.Replace("\r", string.Empty).Split('\n').FirstOrDefault() ?? string.Empty;
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        _ = SendAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _center.Changed -= OnCenterChanged;
        // Never leave an engine running without a surface: stopping is the honest outcome.
        if (_session is not null)
        {
            try { _manager.Stop(_session.TaskId); } catch (Exception) { /* already gone */ }
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        SidebarStatus.Text = text;
    }

    private static string Shorten(string value, int max)
    {
        var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private static Brush Paint(string hex)
    {
        var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
