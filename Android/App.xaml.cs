using FairyAI_Android.Services;
using FairyAI_Android.Services.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FairyAI_Android;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        StartReminders();
        StartNoteSync();
        ApplyStartupTheme();

        var window = ConfigManager.NeedsSetup()
            ? new Window(new NavigationPage(new Pages.SetupWizardPage()))
            : new Window(new AppShell());
        var updates = new SignedUpdateService(AppInfo.Current.VersionString, (version, token) =>
            MainThread.InvokeOnMainThreadAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (window.Page is not { } page) return;
                await page.DisplayAlertAsync("发现新版本", $"Android {version} 已发布，更新信息已通过签名验证。请通过官方渠道手动更新。", "知道了")
                    .WaitAsync(token);
            }));
        window.Activated += (_, _) => updates.StartForeground();
        window.Resumed += (_, _) => updates.StartForeground();
        window.Deactivated += (_, _) => updates.StopForeground();
        window.Stopped += (_, _) => updates.StopForeground();
        window.Destroying += (_, _) => updates.Dispose();
        return window;
    }

    /// <summary>
    /// Applies the saved theme before the first frame.
    ///
    /// 必须同步做完再建窗口。之前这里是 Task.Run 丢给后台线程的，结果是 CreateWindow 已经把
    /// AppShell 和对话页建出来了、主题才换完一半——首屏就出现「底色是浅的、输入栏和文字还是深色」
    /// 这种半旧半新的界面。配置只是一个小 JSON，同步读一次比首帧错色划算得多。
    /// </summary>
    private static void ApplyStartupTheme()
    {
        try
        {
            ThemeManager.ApplyFromConfig();
        }
        catch (Exception ex)
        {
            Log("Theme", ex);
        }
    }

    /// <summary>
    /// Starts the reminder loop once per process. A failure here must not take the app down — the
    /// user would lose chat as well as reminders — so it is logged instead.
    /// </summary>
    private static void StartReminders()
    {
        // Same reasoning as note sync: everything that reads configuration or files happens off the
        // UI thread, so start-up cannot be held up by a lock someone else is holding.
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                services.GetService<ReminderService>()?.Start();
            }
            catch (Exception ex)
            {
                Log("Reminder", ex);
            }
        });
    }

    /// <summary>Note sync only runs when the user turned it on; it is never started silently.</summary>
    private static void StartNoteSync()
    {
        // The switch lives in the config file, and reading the config can take a lock that another
        // thread may be holding. Doing it here — on the UI thread, inside CreateWindow — would put
        // that wait in front of the first frame, so it is handed to a worker.
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                var notes = services.GetService<NoteSyncService>();
                if (notes is not null && ConfigManager.Load().Sync.NotesSyncEnabled) notes.Start();
            }
            catch (Exception ex)
            {
                Log("NoteSync", ex);
            }
        });
    }

    private static void Log(string tag, Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [{tag}] start failed: {ex.GetType().Name}\n");
        }
        catch (IOException)
        {
            // Nothing left to report to.
        }
    }
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }
}
