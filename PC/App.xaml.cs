using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.HomeAssistant;
using MyAiAssistant.Services.Mcp;
using MyAiAssistant.Services.Messaging;
using MyAiAssistant.Services.Theme;
using MyAiAssistant.Services.Transfer;
using MyAiAssistant.Services.Notifications;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;
using MyAiAssistant.ViewModels;
using MyAiAssistant.Windows;

namespace MyAiAssistant;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private FloatingOrbWindow? _orb;
    private DynamicIslandWindow? _island;
    private FairyViewModel? _vm;
    private DispatcherTimer? _inactivityTimer;
    private TrayIconService? _tray;
    private SignedUpdateService? _signedUpdates;
    private ClipboardHistoryService? _clipboardHistory;
    private ClipboardHistoryWindow? _clipboardWindow;
    private OllamaSetupWindow? _ollamaWindow;
    private DevicePairingWindow? _pairingWindow;
    private WebDashboardService? _dashboard;
    private LocalSendReceiveCoordinator? _localSendReceiver;
    private PairingControl? _pairingControl;
    private TransferControl? _transferControl;
    private NotificationBridge? _notificationBridge;
    private NotificationCenter? _notifications;
    private DashboardAccessWindow? _dashboardWindow;
    private ControlCenterWindow? _controlCenterWindow;
    private FairyCodeWindow? _fairyCodeWindow;
    private ModelLibraryWindow? _modelLibraryWindow;
    private DesktopDockWindow? _dock;
    private GlobalKeyboardHook? _keyboardHook;
    private const int InactivityTimeoutMs = 30_000;
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fairy.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log("=== Fairy AI Starting (Dynamic Island Mode) ===");

        try
        {
            // 主题必须在任何窗口创建之前应用：窗口一创建就会解析 DynamicResource，
            // 晚一步的话窗口会先用默认色画一帧。
            ThemeManager.ApplyFromConfig();

            // Check for setup-complete flag (from installer) — show welcome but DON'T skip wizard
            if (e.Args.Contains("--setup-complete"))
            {
                Log("Installer finished — will show setup wizard if needed");
            }

            // Always show setup wizard on first run (or when API key is missing)
            if (ConfigManager.NeedsSetup())
            {
                Log("First run detected — showing setup wizard");
                var wizard = new SetupWizardWindow();
                var result = wizard.ShowDialog();
                if (result != true)
                {
                    Log("Setup cancelled by user");
                    Shutdown();
                    return;
                }
                Log("Setup completed");
            }

            // DI
            var sc = new ServiceCollection();
            sc.AddSingleton<IMiMoAsrService, MiMoAsrService>();
            sc.AddSingleton<IMiMoTtsService, MiMoTtsService>();
            sc.AddSingleton<IAudioPlayerService, AudioPlayerService>();
            sc.AddSingleton<IContinuousSpeechService, HybridSpeechService>();
            sc.AddSingleton<ILlmService, LlmService>();
            sc.AddSingleton<IChatHistoryService, ChatHistoryService>();
            sc.AddSingleton<IOpenClawManager, OpenClawManager>();
            sc.AddSingleton<ITavilySearchService, TavilySearchService>();
            sc.AddSingleton<IOpenClawAgentService, OpenClawAgentService>();
            sc.AddSingleton<IAppMappingService, AppMappingService>();
            sc.AddSingleton<IEverythingService, EverythingService>();
            sc.AddSingleton<IIntentAnalyzer, IntentAnalyzer>();
            sc.AddSingleton<IVisionService, VisionService>();
            sc.AddSingleton<ILive2DService, Live2DService>();
            sc.AddSingleton<IScreenControlService, ScreenControlService>();
            sc.AddSingleton<IAudioConverter, FfmpegAudioConverter>();
            sc.AddSingleton<VectorDatabase>();
            sc.AddSingleton<RAGService>();
            // The embedding model that RAGService uses; it must be registered, because the container
            // does not build unregistered concrete types on its own.
            sc.AddSingleton<MyAiAssistant.Services.Knowledge.QwenEmbeddingService>();
            sc.AddSingleton<AgentService>();
            sc.AddSingleton<LocalModelService>();
            sc.AddSingleton<SecurityAuditService>();
            sc.AddSingleton(_ => new OperationAudit());
            sc.AddSingleton<AgentControlCenter>();
            sc.AddSingleton<ISettingsStore, ConfigSettingsStore>();
            sc.AddSingleton(_ => new MessagingAccountStore());
            sc.AddSingleton(sp => new MessagingAccountService(sp.GetRequiredService<MessagingAccountStore>()));

            // The approval gate is the single door for high-risk actions. Fairy Code engines
            // reach it through the loopback bridge, so a tool call cannot execute without it.
            sc.AddSingleton<PermissionPolicy>();
            sc.AddSingleton(sp => new ApprovalGate(
                sp.GetRequiredService<PermissionPolicy>(),
                sp.GetRequiredService<OperationAudit>()));
            sc.AddSingleton<IFairyCodeModelSource, ConfigFairyCodeModelSource>();
            sc.AddSingleton(sp => new FairyCodeSessionManager(
                sp.GetRequiredService<AgentControlCenter>(),
                sp.GetRequiredService<ApprovalGate>(),
                sp.GetRequiredService<OperationAudit>(),
                sp.GetRequiredService<IFairyCodeModelSource>()));

            // LAN receiving: the coordinator exists even when disabled so the control centre can
            // turn it on without a restart; the listener itself only starts when enabled.
            sc.AddSingleton(sp => new LocalSendReceiveCoordinator(
                sp.GetRequiredService<AgentControlCenter>(),
                sp.GetRequiredService<OperationAudit>(),
                LocalSendSettingsBinder.OptionsFromConfig()));
            sc.AddSingleton(sp => new LocalSendTransferService(
                sp.GetRequiredService<OperationAudit>(), Log));
            sc.AddSingleton<LocalSendSettingsService>();

            // Pairing: the store and the service exist regardless of the setting, so the control
            // centre can turn the door on without a restart; the port only opens when enabled.
            sc.AddSingleton(sp => new PairingService(
                new DeviceGroupStore(PairingService.DefaultDirectory, new AppPairingSecretProtector()),
                new PairingInviteStore()));
            sc.AddSingleton(sp => new PairingEndpointServer(
                sp.GetRequiredService<PairingService>(),
                ConfigManager.Load().Pairing.Port));
            sc.AddSingleton(sp => new PairingControl(
                sp.GetRequiredService<PairingService>(),
                sp.GetRequiredService<PairingEndpointServer>()));

            // Transfers between paired devices. The channel factory has to run on the UI thread
            // because the WebRTC surface is a WebView2 control.
            sc.AddSingleton(sp => new TransferControl(
                sp.GetRequiredService<PairingService>(),
                sp.GetRequiredService<PairingEndpointServer>(),
                sp.GetRequiredService<AgentControlCenter>(),
                CreateTransferChannelAsync));

            // Notifications: one history of the app's own, fed by the same event stream the control
            // centre reads, so the HUD and the dashboard can never disagree about what happened.
            sc.AddSingleton(_ => new NotificationControl());
            sc.AddSingleton(sp => new NotificationCenter(sp.GetRequiredService<NotificationControl>()));
            sc.AddSingleton(sp => new NotificationBridge(
                sp.GetRequiredService<AgentControlCenter>(),
                sp.GetRequiredService<NotificationCenter>()));

            // Home Assistant: an optional client for the user's own instance, never a bundled
            // server. Reads are cached; every service call goes through the approval gate and
            // therefore shows up in the same pending list the dashboard displays.
            sc.AddSingleton(sp => new HomeAssistantControl(
                new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
                new HomeAssistantTokenStore(),
                sp.GetRequiredService<ApprovalGate>(),
                new PendingQueueApprover(sp.GetRequiredService<AgentControlCenter>()),
                HomeAssistantSettingsFromConfig(),
                SaveHomeAssistantSettings,
                ownsHttpClient: true));

            // Ordinary system notifications: UniDesktop API first (it drives WinRT toasts on Windows
            // and org.freedesktop.Notifications on Linux), tray balloon as the fallback. Approvals
            // never leave the app, because a toast cannot carry the answer.
            sc.AddSingleton(sp => new SystemNotificationRelay(
                sp.GetRequiredService<NotificationCenter>(),
                sp.GetRequiredService<NotificationControl>(),
                CreateSystemNotificationChannel()));

            // Task-summary sync: the same token store the LAN service verifies against, so a revoke
            // from the control centre takes effect on the next request.
            sc.AddSingleton<ISummarySyncControl>(_ => new SummarySyncControl(
                PairedDeviceTokens.Shared,
                () => ConfigManager.Load().Sync.SummarySyncEnabled,
                enabled =>
                {
                    var config = ConfigManager.Load();
                    config.Sync.SummarySyncEnabled = enabled;
                    ConfigManager.Save(config);
                },
                () => ConfigManager.Load().Sync.PairControlEnabled,
                enabled =>
                {
                    var config = ConfigManager.Load();
                    config.Sync.PairControlEnabled = enabled;
                    ConfigManager.Save(config);
                },
                () => ConfigManager.Load().Sync.NotesSyncEnabled,
                enabled =>
                {
                    var config = ConfigManager.Load();
                    config.Sync.NotesSyncEnabled = enabled;
                    ConfigManager.Save(config);
                }));

            sc.AddSingleton<IScreenSessionControl>(sp => new ScreenSessionControl(
                sp.GetRequiredService<ScreenSession>()));

            // Optional MCP backend (Windows perception/action). Its tools are published only when the
            // user enabled it, and every call still passes the session state, approval and audit.
            sc.AddSingleton<IMcpStatus>(sp => new McpControl(
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetRequiredService<ScreenSession>(),
                () => sp.GetRequiredService<ApprovalGate>(),
                () =>
                {
                    var mcp = ConfigManager.Load().Mcp;
                    return new McpSettings(mcp.Enabled, mcp.ServerCommand,
                        McpControl.ParseArguments(mcp.ServerArguments), mcp.StartupSeconds);
                }));

            sc.AddSingleton(sp => new WebDashboardService(
                sp.GetRequiredService<AgentControlCenter>(),
                sp.GetRequiredService<OperationAudit>(),
                sp.GetRequiredService<ISettingsStore>(),
                sp.GetRequiredService<MessagingAccountStore>(),
                sp.GetRequiredService<MessagingAccountService>(),
                sp.GetRequiredService<FairyCodeSessionManager>(),
                sp.GetRequiredService<LocalSendSettingsService>(),
                sp.GetRequiredService<LocalSendTransferService>(),
                sp.GetRequiredService<PairingControl>(),
                sp.GetRequiredService<TransferControl>(),
                sp.GetRequiredService<NotificationControl>(),
                sp.GetRequiredService<NotificationCenter>(),
                sp.GetRequiredService<HomeAssistantControl>(),

                sp.GetRequiredService<SystemNotificationRelay>(),
                sp.GetRequiredService<ISummarySyncControl>(),
                sp.GetRequiredService<IScreenSessionControl>(),
                sp.GetRequiredService<IMcpStatus>(),
                appearance: new MyAiAssistant.Services.Appearance.AppearanceControl()));
            sc.AddSingleton<ConnectivityManager>();
            sc.AddSingleton<FairyViewModel>();

            // Function Calling tools & services
            var toolRegistry = new ToolRegistry();
            toolRegistry.Register(new GetCurrentTimeTool());
            toolRegistry.Register(new CalculatorTool());
            toolRegistry.Register(new SystemInfoTool());
            toolRegistry.Register(new ClipboardCopyTool());
            var noteService = new NoteService();
            var reminderService = new ReminderService();
            var shortcutService = new ShortcutService();
            var tokenUsageService = new TokenUsageService();
            var skillService = new SkillService();
            var translateService = new TranslateService();
            toolRegistry.Register(new CreateNoteTool(noteService));
            toolRegistry.Register(new SearchNotesTool(noteService));
            toolRegistry.Register(new ListNotesTool(noteService));
            toolRegistry.Register(new DeleteNoteTool(noteService));
            toolRegistry.Register(new SetReminderTool(reminderService));
            toolRegistry.Register(new ListRemindersTool(reminderService));
            toolRegistry.Register(new CancelReminderTool(reminderService));
            toolRegistry.Register(new CreateShortcutTool(shortcutService));
            toolRegistry.Register(new ListShortcutsTool(shortcutService));
            toolRegistry.Register(new TranslateTool(translateService));
            toolRegistry.Register(new GetTokenUsageTool(tokenUsageService));
            toolRegistry.Register(new SetTokenLimitTool(tokenUsageService));
            toolRegistry.Register(new ListSkillsTool(skillService));
            toolRegistry.Register(new RunSkillTool(skillService));
            var screenControl = new ScreenControlService();

            // One session per app run, so pause/stop from the control centre applies to whatever the
            // agent is doing. Every screen action passes the platform check, the session state and the
            // approval gate before it touches the mouse.
            var screenSession = new ScreenSession();
            sc.AddSingleton(screenSession);
            var screenRunner = new ScreenActionRunner(
                screenControl, screenSession, () => _services.GetRequiredService<ApprovalGate>());

            var visionService = new VisionService();
            var blenderService = new BlenderService();
            var officeService = new OfficeIntegrationService();
            var kbService = new KnowledgeBaseService();
            toolRegistry.Register(new TakeScreenshotTool(visionService));
            toolRegistry.Register(new MouseClickTool(screenRunner));
            toolRegistry.Register(new MouseDoubleClickTool(screenRunner));
            toolRegistry.Register(new MouseRightClickTool(screenRunner));
            toolRegistry.Register(new TypeTextTool(screenRunner));
            toolRegistry.Register(new PressKeyTool(screenRunner));
            toolRegistry.Register(new ScrollTool(screenRunner));
            toolRegistry.Register(new ConvertImageTo3DTool(blenderService));
            toolRegistry.Register(new OpenOfficeDocTool(officeService, kbService));
            toolRegistry.Register(new GetOfficeInfoTool(officeService));
            sc.AddSingleton(toolRegistry);
            sc.AddSingleton(sp => new FunctionCallingService(
                sp.GetRequiredService<ILlmService>(), toolRegistry));
            sc.AddSingleton(noteService);
            sc.AddSingleton(reminderService);
            sc.AddSingleton(shortcutService);
            sc.AddSingleton(tokenUsageService);
            sc.AddSingleton(skillService);
            sc.AddSingleton(translateService);
            _services = sc.BuildServiceProvider();
            Log("DI container built");

            // LAN receiving is opt-in: it opens a port on every interface, so the listener only
            // starts when the user turned it on in configuration or in the control centre.
            _localSendReceiver = _services.GetRequiredService<LocalSendReceiveCoordinator>();
            if (ConfigManager.Load().LocalSend.Enabled)
            {
                if (_localSendReceiver.Start()) Log($"LocalSend 接收端已监听 0.0.0.0:{_localSendReceiver.Port}");
                else Log($"LocalSend 接收端未能启动：{_localSendReceiver.LastError}");
                _services.GetRequiredService<LocalSendTransferService>()
                    .Start(LocalSendSettingsBinder.OptionsFromConfig());
            }

            // Start Web Dashboard
            _dashboard = _services.GetRequiredService<WebDashboardService>();
            _ = StartDashboardAsync(_dashboard);

            // MCP backend is opt-in. When the user configured and enabled it, start the server once
            // and log the outcome; a failure leaves the rest of the app untouched.
            if (ConfigManager.Load().Mcp.Enabled)
            {
                var mcpStatus = _services.GetRequiredService<IMcpStatus>();
                var started = await mcpStatus.StartAsync();
                Log(started.Ok ? $"MCP 后端：{started.Message}" : $"MCP 后端启动失败：{started.Message}");
            }

            // Pairing is opt-in for the same reason as the LAN receiver: it opens a port other
            // devices can reach. It only listens when the user turned it on.
            _pairingControl = _services.GetRequiredService<PairingControl>();
            var pairingStart = _pairingControl.StartIfEnabled();
            Log($"配对：{pairingStart.Message}");

            // Notifications start listening once the control centre exists, and present through the
            // island that is created a little later, so the subscription is set up here and resolved
            // at presentation time.
            _notificationBridge = _services.GetRequiredService<NotificationBridge>();
            _notifications = _services.GetRequiredService<NotificationCenter>();
            _notifications.Presented += OnNotificationPresented;

            // Transfers only make sense with a relay to route signalling through.
            _transferControl = _services.GetRequiredService<TransferControl>();
            if (pairingStart.Started) _transferControl.Start();

            // Log security event
            var auditService = _services.GetRequiredService<SecurityAuditService>();
            var version = typeof(App).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(App).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            auditService.LogEvent("APP_START", $"FairyAI v{version} started");

            // Auto-deploy Blender addon
            BlenderService.DeployAddon();

            // ViewModel
            _vm = _services.GetRequiredService<FairyViewModel>();
            await _vm.InitializeAsync();
            Log("ViewModel initialized");

            // Start connectivity services
            var syncManager = _services.GetRequiredService<ConnectivityManager>();

            // A paired phone may read task summaries — only when the user turned it on, and only
            // this shape: id, scrubbed title, state, time. No prompts, outputs or paths exist here.
            var taskCenter = _services.GetRequiredService<AgentControlCenter>();
            syncManager.SummarySource = () => taskCenter.Tasks()
                .Select(task => new TaskSummary(task.Id, task.Title, task.State.ToString(), task.UpdatedAt))
                .ToArray();

            // A paired phone may also read the task list and answer pending approvals, behind its own
            // switch. Answering goes through AgentControlCenter, the same door the dashboard uses.
            syncManager.PairedTasks = new PairedTaskBridge(
                taskCenter,
                PairedDeviceTokens.Shared,
                () => ConfigManager.Load().Sync.PairControlEnabled);

            // Notes sync uses the same paired channel: same token, its own switch in settings, and the
            // same NoteService the desktop tools write to, so both sides see one list.
            syncManager.PairedNotes = new PairedNoteBridge(
                noteService,
                PairedDeviceTokens.Shared,
                () => ConfigManager.Load().Sync.NotesSyncEnabled);

            await syncManager.StartAllAsync();
            Log("Connectivity services started");

            // Windows
            _orb = new FloatingOrbWindow();
            _island = new DynamicIslandWindow();

            _vm.SetWindows(_orb, _island);

            // Orb click → toggle island
            _orb.OrbClicked += () => Dispatcher.Invoke(() => _vm.OnOrbClicked());

            // Show orb (floating at top center)
            _orb.Show();
            Log("Orb window shown");

            // Intercept window close → minimize to tray instead of exiting
            _orb.Closing += (_, _) => MinimizeToTray();
            _island.Closing += (_, _) => MinimizeToTray();

            // Clipboard history
            _clipboardHistory = new ClipboardHistoryService();
            _clipboardHistory.Start();
            Log("Clipboard history started");

            // System tray
            _tray = new TrayIconService(
                _orb, _island,
                enable => AutoStartService.SetEnabled(enable),
                ShowClipboardHistory,
                ShowOllamaSetup,
                ShowDevicePairing,
                ShowDashboardAccess,
                ShowFairyCode);
            _tray.ExitRequested += () => Dispatcher.Invoke(Shutdown);
            _tray.Show();
            Log("Tray icon shown");

            _dock = new DesktopDockWindow(OnDockAction);
            _dock.Show();
            Log("Desktop dock shown");

            _signedUpdates = new SignedUpdateService(
                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
            // An update is an ordinary notification, not a task event: it goes through the same
            // contract as everything else, so it lands in the app's history and in the OS
            // notification only when the user allows system notifications.
            _signedUpdates.UpdateAvailable += version =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() => _notifications?.Add(
                    NotificationKind.System,
                    "仙灵有新版本",
                    $"发现版本 {version}，清单及条目签名已验证。请手动更新；不会自动下载或安装。",
                    severity: NotificationSeverity.Info,
                    dedupeKey: "update:" + version)));
            };
            _signedUpdates.Start();

            // Start reminder service
            reminderService.ReminderFired += OnReminderFired;
            reminderService.Start();
            Log("Reminder service started");

            // Token usage limit alerts
            tokenUsageService.LimitAlert += (title, msg) =>
            {
                _tray?.Notify(title, msg, System.Windows.Forms.ToolTipIcon.Warning);
                Dispatcher.Invoke(() => MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning));
            };

            // Global hotkey: Alt+Space to summon/hide
            _keyboardHook = new GlobalKeyboardHook();
            _keyboardHook.AltSpacePressed += () => Dispatcher.Invoke(ToggleMainVisibility);
            _keyboardHook.Start();
            Log("Global hotkey Alt+Space registered");

            // Inactivity timer
            _inactivityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(InactivityTimeoutMs)
            };
            _inactivityTimer.Tick += (_, _) =>
            {
                _inactivityTimer.Stop();
                Dispatcher.Invoke(() => _vm?.Deactivate());
            };

            Log("Fairy AI ready — click the orb to start");
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            MessageBox.Show($"启动失败:\n{ex.Message}", "Fairy AI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MinimizeToTray()
    {
        // Cancel close, hide to tray instead
        Dispatcher.Invoke(() =>
        {
            _orb?.Hide();
            _island?.Hide();
            _vm?.Deactivate();
            _tray?.Notify("Fairy AI 仍在运行", "已最小化到系统托盘，双击图标恢复", System.Windows.Forms.ToolTipIcon.Info);
            Log("Minimized to tray");
        });
    }

    /// <summary>Toggle main windows visibility (used by Alt+Space hotkey).</summary>
    private void ToggleMainVisibility()
    {
        if (_orb == null) return;

        if (_orb.IsVisible || _island?.IsVisible == true)
        {
            _orb.Hide();
            _island?.Hide();
            _vm?.Deactivate();
            Log("Hidden via Alt+Space");
        }
        else
        {
            _orb.Show();
            Log("Shown via Alt+Space");
        }
    }

    private void OnReminderFired(ReminderItem reminder)
    {
        Dispatcher.Invoke(() =>
        {
            _tray?.Notify($"提醒: {reminder.Title}", reminder.Message, System.Windows.Forms.ToolTipIcon.Info);
            _island?.ShowResponse($"⏰ 提醒: {reminder.Title}\n{reminder.Message}");
        });
    }

    private void ShowClipboardHistory()
    {
        Dispatcher.Invoke(() =>
        {
            if (_clipboardWindow == null)
            {
                _clipboardWindow = new ClipboardHistoryWindow(_clipboardHistory!);
                _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
            }
            _clipboardWindow.Show();
            _clipboardWindow.Activate();
        });
    }

    private void ShowOllamaSetup()
    {
        Dispatcher.Invoke(() =>
        {
            if (_ollamaWindow == null)
            {
                _ollamaWindow = new OllamaSetupWindow();
                _ollamaWindow.Closed += (_, _) => _ollamaWindow = null;
            }
            _ollamaWindow.Show();
            _ollamaWindow.Activate();
        });
    }

    private async Task StartDashboardAsync(WebDashboardService dashboard)
    {
        // Report the real outcome: a port conflict must be visible instead of being
        // reported as success and leaving the user with a dead address.
        var started = await dashboard.StartAsync();
        Log(started
            ? $"Web dashboard listening on {dashboard.Url}"
            : $"Web dashboard failed to bind {dashboard.Url}");
    }

    private void ShowControlCenter()
    {
        Dispatcher.Invoke(() =>
        {
            if (_dashboard is null) return;
            if (_controlCenterWindow == null)
            {
                _controlCenterWindow = new ControlCenterWindow(_dashboard.Url, _dashboard.ControlToken);
                _controlCenterWindow.Closed += (_, _) => _controlCenterWindow = null;
            }
            _controlCenterWindow.Show();
            _controlCenterWindow.Activate();
        });
    }

    private void ShowModelLibrary()
    {
        Dispatcher.Invoke(() =>
        {
            if (_modelLibraryWindow is null)
            {
                _modelLibraryWindow = new ModelLibraryWindow();
                _modelLibraryWindow.Closed += (_, _) => _modelLibraryWindow = null;
            }
            _modelLibraryWindow.Show();
            _modelLibraryWindow.Activate();
        });
    }

    private void OnDockAction(DockAction action)
    {
        switch (action)
        {
            case DockAction.ControlCenter: ShowControlCenter(); break;
            case DockAction.Chat: Dispatcher.Invoke(() => _orb?.Show()); break;
            // Fairy Code is a terminal, not a page: the dock opens the session surface where
            // the engine's output and its approval prompts actually appear.
            case DockAction.FairyCode: ShowFairyCode(); break;
            case DockAction.ModelLibrary: ShowModelLibrary(); break;
        }
    }

    private void ShowFairyCode()
    {
        Dispatcher.Invoke(() =>
        {
            if (_services?.GetService<FairyCodeSessionManager>() is not { } manager) return;
            if (_fairyCodeWindow is null)
            {
                var center = _services.GetRequiredService<AgentControlCenter>();
                _fairyCodeWindow = new FairyCodeWindow(center, manager, LastWorkspace());
                _fairyCodeWindow.Closed += (_, _) =>
                {
                    SaveWorkspace(_fairyCodeWindow?.WorkspaceField.Text);
                    _fairyCodeWindow = null;
                };
            }
            _fairyCodeWindow.Show();
            _fairyCodeWindow.Activate();
        });
    }

    /// <summary>Remembers the last workspace so a reopened terminal lands where the user left it.</summary>
    private static string? LastWorkspace()
    {
        try
        {
            var path = WorkspaceStatePath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception) { return null; }
    }

    private static void SaveWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        try
        {
            var path = WorkspaceStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, workspace.Trim());
        }
        catch (Exception) { /* remembering a path is never worth failing a close over */ }
    }

    private static string WorkspaceStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FairyAI", "fairycode-workspace.txt");

    /// <summary>
    /// Picks the system notification channel: UniDesktop API when its library is present, otherwise
    /// the tray balloon. The choice is logged once at startup so "why did I get a balloon instead of
    /// a toast" is answerable from the log.
    /// </summary>
    private static ISystemNotificationChannel CreateSystemNotificationChannel()
    {
        var uda = UdaSystemNotificationChannel.TryCreate(null, null, out var udaError);
        if (uda is not null)
        {
            Log($"系统通知通道：UniDesktop ({uda.LoadedFrom})");
            return uda;
        }

        Log($"系统通知通道：托盘气泡（UDA 不可用：{udaError}）");
        return new DelegateSystemNotificationChannel(ShowSystemNotification);
    }

    /// <summary>
    /// Windows side of the ordinary system notification: the tray icon owns the balloon and the shell
    /// turns it into an Action Center notification. When the tray is not up yet this reports failure,
    /// so the relay records "未显示" instead of pretending a message reached the user.
    /// </summary>
    private static bool ShowSystemNotification(string title, string body, NotificationSeverity severity)
    {
        if (Application.Current is not App app || app._tray is null) return false;

        var icon = severity switch
        {
            NotificationSeverity.Error => System.Windows.Forms.ToolTipIcon.Error,
            NotificationSeverity.Warning => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Info,
        };

        if (app.Dispatcher.CheckAccess())
        {
            app._tray.Notify(title, body, icon);
            return true;
        }

        app.Dispatcher.BeginInvoke(new Action(() => app._tray?.Notify(title, body, icon)));
        return true;
    }

    private static HomeAssistantSettings HomeAssistantSettingsFromConfig()
    {
        var config = ConfigManager.Load().HomeAssistant;
        return new HomeAssistantSettings(config.ServerUrl, config.ClientId, config.RedirectUri);
    }

    private static void SaveHomeAssistantSettings(HomeAssistantSettings settings)
    {
        var config = ConfigManager.Load();
        config.HomeAssistant.ServerUrl = settings.ServerUrl;
        config.HomeAssistant.ClientId = settings.ClientId;
        config.HomeAssistant.RedirectUri = settings.RedirectUri;
        ConfigManager.Save(config);
    }

    /// <summary>
    /// Creates one WebRTC channel. It has to happen on the UI thread: the surface is a WebView2
    /// control, and building it anywhere else fails with a COM apartment error.
    /// </summary>
    private static async Task<ITransferChannel> CreateTransferChannelAsync(CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher
                         ?? throw new InvalidOperationException("没有 UI 线程，无法建立 WebRTC 通道");
        var cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview2_transfer");

        return await dispatcher.InvokeAsync(() => WebRtcPeer.CreateAsync(cache, cancellationToken))
            .Task.Unwrap().ConfigureAwait(true);
    }
    /// <summary>
    /// Shows a notification on the island. The island is created slightly later than the notification
    /// services, so this tolerates not having one yet instead of dropping the notification.
    /// </summary>
    private void OnNotificationPresented(NotificationEvent notification)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_island is null)
            {
                Log($"通知（岛未就绪）：{notification.Describe()}");
                return;
            }

            try
            {
                _island.ShowResponse($"{notification.Title}{Environment.NewLine}{notification.Body}");
                _island.ShowIsland();
            }
            catch (InvalidOperationException ex)
            {
                Log($"通知展示失败：{ex.Message}");
            }
        }));
    }
    private void ShowDashboardAccess()
    {
        Dispatcher.Invoke(() =>
        {
            if (_dashboard is null) return;
            if (_dashboardWindow == null)
            {
                _dashboardWindow = new DashboardAccessWindow(_dashboard.Url, _dashboard.ControlToken);
                _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
            }
            _dashboardWindow.Show();
            _dashboardWindow.Activate();
        });
    }

    private void ShowDevicePairing()
    {
        Dispatcher.Invoke(() =>
        {
            if (_pairingWindow == null)
            {
                var connectivity = _services.GetRequiredService<ConnectivityManager>();
                _pairingWindow = new DevicePairingWindow(connectivity);
                _pairingWindow.Closed += (_, _) => _pairingWindow = null;
            }
            _pairingWindow.Show();
            _pairingWindow.Activate();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("=== Fairy AI Shutting Down ===");
        _inactivityTimer?.Stop();
        _keyboardHook?.Dispose();
        _signedUpdates?.Dispose();
        _tray?.Dispose();
        _clipboardHistory?.Dispose();
        var syncManager = _services?.GetService<ConnectivityManager>();
        syncManager?.Dispose();
        _services?.GetService<FairyCodeSessionManager>()?.Dispose();
        _localSendReceiver?.Dispose();
        _services?.GetService<LocalSendTransferService>()?.Dispose();
        if (_notifications is not null) _notifications.Presented -= OnNotificationPresented;
        _notificationBridge?.Dispose();
        _transferControl?.Dispose();
        _pairingControl?.Dispose();
        _vm?.Dispose();
        base.OnExit(e);
    }
}
