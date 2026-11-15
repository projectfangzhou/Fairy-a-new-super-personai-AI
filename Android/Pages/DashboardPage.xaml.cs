using FairyAI_Android.Services;
using FairyAI_Android.Services.Performance;
using FairyAI_Android.Services.Reminders;
using FairyAI_Android.Services.Screen;

namespace FairyAI_Android.Pages;

/// <summary>
/// The phone's own overview: what is connected, what is waiting, what the phone is allowed to do,
/// and how it actually performs. Chat stays the first tab — this page exists so that connection and
/// permission state has somewhere to live that is not a settings list.
/// </summary>
public partial class DashboardPage : ContentPage
{
    private readonly PairedDeskService _desk;
    private readonly ScreenControl _screen;
    private readonly PerformanceMonitor _performance;
    private readonly FairyAI_Android.Services.Usage.TokenUsageService _usage;
    private readonly NoteService _notes;
    private readonly ReminderService _reminders;

    public DashboardPage(PairedDeskService desk, ScreenControl screen, PerformanceMonitor performance,
        Services.Usage.TokenUsageService usage, NoteService notes, ReminderService reminders)
    {
        InitializeComponent();

        _desk = desk;
        _screen = screen;
        _performance = performance;
        _usage = usage;
        _notes = notes;
        _reminders = reminders;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadLocalState();
        _ = RefreshDeskAsync();
        ShowPerformance();
        TokenReportLabel.Text = _usage.Describe();
        ShowNotes();
    }

    /// <summary>How much the phone is remembering on its own, and when it will next speak up.</summary>
    private void ShowNotes()
    {
        try
        {
            var reminder = _reminders.All().FirstOrDefault(item => item.Enabled);
            var next = reminder is null ? "没有待提醒" : "下次 " + ReminderSchedule.DescribeWhen(reminder.DueAt);
            NotesValueLabel.Text = $"{_notes.Count} 条笔记 · {next}";
        }
        catch (Exception)
        {
            NotesValueLabel.Text = "读取失败";
        }
    }

    private void LoadLocalState()
    {
        try
        {
            var config = ConfigManager.Load();
            var name = string.IsNullOrWhiteSpace(config.Personality.Name) ? "仙灵" : config.Personality.Name;
            GreetingLabel.Text = "你好，" + name;

            ModelLabel.Text = string.IsNullOrWhiteSpace(config.LLM.Model)
                ? "还没有配置文本模型：到设置里选一个供应商和模型名称"
                : $"{config.LLM.Provider} · {config.LLM.Model}";

            SummaryValueLabel.Text = config.Sync.SummarySyncEnabled ? "已开启" : "未开启";
        }
        catch
        {
            GreetingLabel.Text = "看板";
            ModelLabel.Text = "配置读取失败";
        }

        var status = _screen.Status;
        ScreenValueLabel.Text = status.Availability switch
        {
            ShizukuAvailability.Authorized => "可用",
            ShizukuAvailability.NotAuthorized => "未授权",
            ShizukuAvailability.NotRunning => "服务未运行",
            ShizukuAvailability.NotInstalled => "未安装",
            _ => "暂时不可用",
        };

        PcValueLabel.Text = _desk.IsConfigured ? "已配对" : "未配对";
        PendingValueLabel.Text = "—";
    }

    private async Task RefreshDeskAsync()
    {
        if (!_desk.IsConfigured)
        {
            PendingValueLabel.Text = "—";
            return;
        }

        var (ok, message, desk) = await _desk.FetchAsync();
        if (!ok)
        {
            PcValueLabel.Text = "读取失败";
            PendingValueLabel.Text = "—";
            ModelLabel.Text = message;
            return;
        }

        PcValueLabel.Text = "已配对";
        PendingValueLabel.Text = desk is null ? "—" : desk.Pending.Count.ToString();
    }

    private void ShowPerformance()
    {
        if (!_performance.IsSupported)
        {
            FrameReportLabel.Text = "这台设备上还没有帧时间采样通道（目前只有 Android 有）。";
            MemoryReportLabel.Text = "";
            return;
        }

        FrameReportLabel.Text = _performance.Describe(_performance.LastReport);
        MemoryReportLabel.Text = _performance.LastReport.HasSamples ? _performance.DescribeMemory() : "";
    }

    private async void OnSampleClicked(object? sender, EventArgs e)
    {
        if (!_performance.IsSupported)
        {
            FrameReportLabel.Text = "这台设备上还没有帧时间采样通道。";
            return;
        }

        FrameReportLabel.Text = "测量中，请保持这个页面在前台…";
        var (ok, message) = await _performance.SampleAsync(TimeSpan.FromSeconds(3));

        FrameReportLabel.Text = message;
        MemoryReportLabel.Text = ok ? _performance.DescribeMemory() : "";
    }

    private async void OnDevicesTapped(object? sender, TappedEventArgs e)
    {
        if (await AppNavigation.PushAsync<DevicesPage>(Navigation)) return;

        await DisplayAlertAsync("设备连接", "打不开设备连接页，请重启应用后再试。", "确定");
    }

    private async void OnTasksTapped(object? sender, TappedEventArgs e)
    {
        if (await AppNavigation.GoToTabAsync("//tasks")) return;

        await DisplayAlertAsync("任务", "打不开任务页，请重启应用后再试。", "确定");
    }

    private async void OnNotesTapped(object? sender, TappedEventArgs e)
    {
        if (await AppNavigation.PushAsync<NotesPage>(Navigation)) return;

        await DisplayAlertAsync("笔记与提醒", "打不开笔记与提醒页，请重启应用后再试。", "确定");
    }
}
