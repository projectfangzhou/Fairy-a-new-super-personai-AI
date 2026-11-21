using FairyAI_Android.Services;
using FairyAI_Android.Services.Theme;
using FairyAI_Android.Services.Screen;
using FairyAI_Android.Services.Vision;
using DeviceInfo = FairyAI_Android.Services.DeviceInfo;

namespace FairyAI_Android.Pages;

/// <summary>
/// One place for everything about "what this phone is connected to and what it is allowed to do":
/// pairing with a PC, the task summary it may read, and the screen capabilities it may use locally.
///
/// It lives outside Settings on purpose. Settings is for what the assistant sounds like and which
/// model it uses; connections and permissions are reached from the dashboard, because those are the
/// things a user comes back to change.
/// </summary>
public partial class DevicesPage : ContentPage
{
    private readonly ILocalSendService _localSend;
    private readonly SummarySyncService _summarySync;
    private readonly ScreenControl _screen;
    private readonly VisionService _vision;
    private readonly NoteSyncService _noteSync;

    private bool _loadingSummarySync;
    private bool _loadingScreen;
    private bool _loadingNoteSync;
    private bool _looking;
    private List<DeviceInfo> _devices = new();
    private DeviceInfo? _selectedDevice;
    private readonly Dictionary<ScreenCapability, Switch> _grantSwitches = new();

    public DevicesPage(ILocalSendService localSend, SummarySyncService summarySync, ScreenControl screen,
        VisionService vision, NoteSyncService noteSync)
    {
        InitializeComponent();

        _localSend = localSend;
        _summarySync = summarySync;
        _screen = screen;
        _vision = vision;
        _noteSync = noteSync;

        BuildGrantRows();
        DevicesList.SelectionChanged += OnDeviceSelected;

        // Shizuku answers its permission dialog asynchronously; refresh so the status does not keep
        // claiming "not authorized" after the user already allowed it.
        _screen.StatusChanged += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshScreenSection);
        _noteSync.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshNotesSync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        LoadEntries();
        RefreshSummarySync();
        RefreshScreenSection();
        RefreshVisionSection();
        RefreshNotesSync();

        // Known devices are persisted, so show them without waiting for a discovery round.
        _ = LoadKnownDevicesAsync();

        if (ConfigManager.Load().Sync.SummarySyncEnabled) _summarySync.Start();
    }

    private async Task LoadKnownDevicesAsync()
    {
        try
        {
            _devices = await _localSend.GetDevicesAsync();
            DevicesList.ItemsSource = _devices;
        }
        catch (Exception ex)
        {
            LocalSendStatus.Text = $"读取已知设备失败: {ex.Message}";
        }
    }

    private void LoadEntries()
    {
        try
        {
            var sync = ConfigManager.Load().Sync;
            DeviceNameEntry.Text = string.IsNullOrWhiteSpace(sync.DeviceName) ? "FairyAI-Android" : sync.DeviceName;
            RelayUrlEntry.Text = sync.SignalRUrl ?? "";
        }
        catch
        {
            // A broken config should not stop this page from opening.
        }
    }

    private void BuildGrantRows()
    {
        GrantList.Clear();
        _grantSwitches.Clear();

        foreach (var capability in ScreenCapabilityCatalog.All)
        {
            var toggle = new Switch { IsToggled = false, VerticalOptions = LayoutOptions.Center };
            var captured = capability;
            toggle.Toggled += (_, e) => OnGrantToggled(captured, e.Value);

            var row = new HorizontalStackLayout { Spacing = 10 };
            row.Add(toggle);
            row.Add(new Label
            {
                Text = ScreenCapabilityCatalog.Label(capability),
                TextColor = Glass.ReadColor("Theme.TextPrimary"),
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
            });

            GrantList.Add(row);
            _grantSwitches[capability] = toggle;
        }
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e) =>
        _selectedDevice = e.CurrentSelection.FirstOrDefault() as DeviceInfo;

    private async void OnStartLocalSend(object? sender, EventArgs e)
    {
        try
        {
            if (_localSend.IsRunning)
            {
                await _localSend.StopAsync();
                StartLocalSendBtn.Text = "启动发现服务";
                LocalSendStatus.Text = "已停止";
            }
            else
            {
                await _localSend.StartAsync();
                StartLocalSendBtn.Text = "停止发现服务";
                LocalSendStatus.Text = "运行中…";
                _ = DiscoverDevicesLoop();
            }
        }
        catch (Exception ex)
        {
            LocalSendStatus.Text = $"启动失败: {ex.Message}";
        }
    }

    private async void OnDiscoverDevices(object? sender, EventArgs e)
    {
        if (!_localSend.IsRunning)
        {
            LocalSendStatus.Text = "请先启动发现服务";
            return;
        }

        LocalSendStatus.Text = "搜索中…";
        _devices = await _localSend.GetDevicesAsync();
        DevicesList.ItemsSource = _devices;
        LocalSendStatus.Text = $"找到 {_devices.Count} 台设备";
    }

    private async void OnAddRemoteDevice(object? sender, EventArgs e)
    {
        var input = RemoteDeviceEntry.Text?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            LocalSendStatus.Text = "请输入 IP/域名";
            return;
        }

        LocalSendStatus.Text = $"连接 {input}…";
        var host = input.Contains(':') ? input.Split(':')[0] : input;
        var port = 9877;
        if (input.Contains(':')) int.TryParse(input.Split(':')[1], out port);

        await _localSend.AddKnownDeviceAsync(host, port);
        _devices = await _localSend.GetDevicesAsync();
        DevicesList.ItemsSource = _devices;
        RemoteDeviceEntry.Text = "";
        LocalSendStatus.Text = $"已添加, 共 {_devices.Count} 台设备";
    }

    private async Task DiscoverDevicesLoop()
    {
        while (_localSend.IsRunning)
        {
            try
            {
                _devices = await _localSend.GetDevicesAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DevicesList.ItemsSource = _devices;
                    if (_devices.Count > 0) LocalSendStatus.Text = $"找到 {_devices.Count} 台设备";
                });
            }
            catch
            {
                // Discovery is best-effort; the loop keeps going.
            }

            await Task.Delay(5000);
        }
    }

    private async void OnPairDevice(object? sender, EventArgs e)
    {
        if (_selectedDevice == null)
        {
            LocalSendStatus.Text = "请先选择设备";
            return;
        }

        var code = PairingCodeEntry.Text?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length != 6)
        {
            LocalSendStatus.Text = "请输入 6 位配对码";
            return;
        }

        LocalSendStatus.Text = $"正在配对 {_selectedDevice.DeviceName}…";
        var ok = await _localSend.PairWithDeviceAsync(_selectedDevice, code);
        LocalSendStatus.Text = ok
            ? $"已配对 {_selectedDevice.DeviceName}"
            : "配对失败, 请检查配对码";

        if (ok) RefreshSummarySync();
    }

    private async void OnSyncDevice(object? sender, EventArgs e)
    {
        if (_selectedDevice == null)
        {
            LocalSendStatus.Text = "请先选择设备";
            return;
        }

        var code = PairingCodeEntry.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            LocalSendStatus.Text = "请输入配对码";
            return;
        }

        LocalSendStatus.Text = $"正在同步 {_selectedDevice.DeviceName} 的配置…";
        var configJson = await _localSend.RequestSyncConfigAsync(_selectedDevice, code);
        if (configJson != null)
        {
            var config = ConfigManager.ApplySyncedConfig(ConfigManager.Load(), configJson);
            ConfigManager.Save(config);
            LoadEntries();
            LocalSendStatus.Text = $"同步完成! 人格: {config.Personality.Name}";
        }
        else
        {
            LocalSendStatus.Text = "同步失败, 可能需要先配对";
        }
    }

    private void OnSaveConnectionClicked(object? sender, EventArgs e)
    {
        var config = ConfigManager.Load();
        config.Sync.DeviceName = string.IsNullOrWhiteSpace(DeviceNameEntry.Text)
            ? "FairyAI-Android"
            : DeviceNameEntry.Text!.Trim();
        config.Sync.SignalRUrl = RelayUrlEntry.Text?.Trim() ?? "";
        ConfigManager.Save(config);

        LocalSendStatus.Text = "连接设置已保存";
    }

    private void RefreshSummarySync()
    {
        _loadingSummarySync = true;
        try
        {
            var sync = ConfigManager.Load().Sync;
            SummarySyncSwitch.IsToggled = sync.SummarySyncEnabled;
            SummarySyncStateLabel.Text = DescribeSummarySyncState(sync);
        }
        finally
        {
            _loadingSummarySync = false;
        }
    }

    private void OnSummarySyncToggled(object? sender, ToggledEventArgs e)
    {
        if (_loadingSummarySync) return;

        var config = ConfigManager.Load();
        config.Sync.SummarySyncEnabled = e.Value;
        ConfigManager.Save(config);

        if (e.Value)
        {
            _summarySync.Start();
            SummarySyncStateLabel.Text = "已开启，等待电脑下一次允许";
        }
        else
        {
            _summarySync.Stop();
            SummarySyncStateLabel.Text = "已关闭";
        }
    }

    private async void OnSummarySyncCheckClicked(object? sender, EventArgs e)
    {
        SummarySyncStateLabel.Text = "正在检查…";
        var (ok, message) = await _summarySync.PollOnceAsync();
        SummarySyncStateLabel.Text = message;
        SummarySyncStateLabel.TextColor = ok ? Glass.ReadColor("Theme.Success") : Glass.ReadColor("Theme.Error");
    }

    private void RefreshScreenSection()
    {
        _loadingScreen = true;
        try
        {
            ScreenEnabledSwitch.IsToggled = _screen.IsEnabled;

            foreach (var (capability, toggle) in _grantSwitches)
                toggle.IsToggled = _screen.IsGranted(capability);

            var status = _screen.Status;
            ScreenStatusLabel.Text = DescribeAvailability(status);
            ScreenStatusLabel.TextColor = status.CanInject
                ? Glass.ReadColor("Theme.Success")
                : Glass.ReadColor("Theme.TextSecondary");
        }
        finally
        {
            _loadingScreen = false;
        }

        ScreenResultLabel.Text = "";
    }

    private void OnScreenEnabledToggled(object? sender, ToggledEventArgs e)
    {
        if (_loadingScreen) return;

        _screen.IsEnabled = e.Value;
        RefreshScreenSection();
    }

    private void OnGrantToggled(ScreenCapability capability, bool granted)
    {
        if (_loadingScreen) return;

        _screen.SetGranted(capability, granted);

        // Withdrawing a grant must be visible immediately, so show what is now blocked.
        var decision = _screen.Inspect(capability);
        ScreenResultLabel.Text = decision.Allowed
            ? $"「{ScreenCapabilityCatalog.Label(capability)}」已允许"
            : $"「{ScreenCapabilityCatalog.Label(capability)}」现在会被拦下：{decision.Message}";
        ScreenResultLabel.TextColor = decision.Allowed
            ? Glass.ReadColor("Theme.Success")
            : Glass.ReadColor("Theme.Error");
    }

    private void OnOpenShizuku(object? sender, EventArgs e)
    {
        _screen.OpenShizukuApp(out var message);
        RefreshScreenSection();
        ScreenResultLabel.Text = message;
        ScreenResultLabel.TextColor = Glass.ReadColor("Theme.TextSecondary");
    }

    private async void OnCheckScreenChain(object? sender, EventArgs e)
    {
        ScreenResultLabel.Text = "检查中…";
        ScreenResultLabel.TextColor = Glass.ReadColor("Theme.TextSecondary");

        // Runs the real chain (policy → availability → grant → approval → bridge) for a read-only
        // action, so the result says where it stops instead of asserting that it works.
        var decision = await _screen.RunAsync(new ScreenAction(ScreenCapability.ReadScreen));

        if (decision.Allowed)
        {
            ScreenResultLabel.Text = string.IsNullOrWhiteSpace(decision.Message)
                ? "链路可用：这次操作已经执行。"
                : "链路可用：" + decision.Message;
            ScreenResultLabel.TextColor = Glass.ReadColor("Theme.Success");
            return;
        }

        ScreenResultLabel.Text = "现在会停在：" + decision.Message;
        ScreenResultLabel.TextColor = Glass.ReadColor("Theme.Error");
    }

    private static string DescribeAvailability(ShizukuProbeResult status) => status.Availability switch
    {
        ShizukuAvailability.Authorized => "Shizuku 已授权，可以使用下面的能力。",
        ShizukuAvailability.NotAuthorized => "Shizuku 在运行，但还没有授权给仙灵。",
        ShizukuAvailability.NotRunning => "Shizuku 已安装，但服务没有在运行。",
        ShizukuAvailability.NotInstalled => "这台设备上没有 Shizuku。",
        _ => "本构建读不到 Shizuku 的授权状态：还没有绑定它的官方 SDK。",
    };

    private static string DescribeSummarySyncState(SyncConfig sync)
    {
        if (!sync.SummarySyncEnabled) return "未开启";

        if (string.IsNullOrWhiteSpace(sync.SummarySyncEndpoint) || string.IsNullOrWhiteSpace(sync.SummarySyncToken))
            return "还没有配对的电脑：先在设备列表里配对一次";

        return "已开启 · 电脑 " + sync.SummarySyncEndpoint;
    }

    /// <summary>Which vision endpoint would be called right now — read from config, never guessed.</summary>
    private void RefreshVisionSection()
    {
        try
        {
            var config = ConfigManager.Load();
            var (target, reason) = VisionTargetSelector.Select(
                new VisionSettings(config.Vision.Provider, config.Vision.BaseUrl, config.Vision.Model, config.Vision.ApiKey)
                {
                    SeesImages = config.Vision.SeesImages == true,
                },
                new MultimodalSettings(config.CustomPlatform.MultimodalEnabled,
                    config.CustomPlatform.MultimodalBaseUrl,
                    config.CustomPlatform.MultimodalModel,
                    config.CustomPlatform.MultimodalApiKey),
                new VisionSettings(config.LLM.Provider, config.LLM.BaseUrl, config.LLM.Model, config.LLM.ApiKey)
                {
                    SeesImages = config.LLM.SeesImages == true,
                });

            VisionStateLabel.Text = target is null
                ? reason
                : $"视觉模型：{target.Name} · {target.Model}";
        }
        catch (Exception)
        {
            VisionStateLabel.Text = "读取视觉模型配置失败";
        }
    }

    private async void OnLookAtScreenClicked(object? sender, EventArgs e)
    {
        if (_looking) return;
        _looking = true;

        VisionResultLabel.Text = "准备中…";
        VisionResultLabel.TextColor = Glass.ReadColor("Theme.TextSecondary");

        try
        {
            var outcome = await _vision.LookAsync(
                question: null,
                onStage: stage => MainThread.BeginInvokeOnMainThread(() => VisionResultLabel.Text = stage));
            VisionResultLabel.Text = outcome.Message;
            VisionResultLabel.TextColor = Color.FromArgb(outcome.Ok ? "#E9E9E9" : "#FCA5A5");
        }
        catch (Exception ex)
        {
            VisionResultLabel.Text = "识屏失败：" + ex.Message;
            VisionResultLabel.TextColor = Glass.ReadColor("Theme.Error");
        }
        finally
        {
            _looking = false;
            RefreshVisionSection();
        }
    }

    private void RefreshNotesSync()
    {
        var enabled = ConfigManager.Load().Sync.NotesSyncEnabled;

        _loadingNoteSync = true;
        NotesSyncSwitch.IsToggled = enabled;
        _loadingNoteSync = false;

        NotesSyncStateLabel.Text = enabled
            ? _noteSync.Describe()
            : "未开启（本机笔记只在这台手机上）";
    }

    private void OnNotesSyncToggled(object? sender, ToggledEventArgs e)
    {
        if (_loadingNoteSync) return;

        var config = ConfigManager.Load();
        config.Sync.NotesSyncEnabled = e.Value;
        ConfigManager.Save(config);

        if (e.Value) _noteSync.Start();
        else _noteSync.Stop();

        NotesSyncResultLabel.Text = "";
        RefreshNotesSync();
    }

    private async void OnNotesSyncNowClicked(object? sender, EventArgs e)
    {
        NotesSyncResultLabel.Text = "同步中…";
        NotesSyncResultLabel.TextColor = Glass.ReadColor("Theme.TextSecondary");

        var outcome = await _noteSync.SyncOnceAsync();

        NotesSyncResultLabel.Text = outcome.Message;
        NotesSyncResultLabel.TextColor = Color.FromArgb(outcome.Ok ? "#6EE7B7" : "#FCA5A5");
        RefreshNotesSync();
    }
}
