using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FairyAI_Android.Services;
using FairyAI_Android.Services.Performance;
using FairyAI_Android.Services.Screen;
using FairyAI_Android.ViewModels;
using FairyAI_Android.Pages;

namespace FairyAI_Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Services
        builder.Services.AddSingleton<ILlmService, LlmService>();
        builder.Services.AddSingleton<IIntentAnalyzer, IntentAnalyzer>();
        builder.Services.AddSingleton<ISearchService, SearchService>();

        // Search backends, cheapest first: the engine Android already ships, then the plain-HTML
        // endpoint. Neither needs a key, so search works without any paid API.
#if ANDROID
        builder.Services.AddSingleton<FairyAI_Android.Services.Search.IWebSearchBackend, FairyAI_Android.Services.Search.AndroidWebViewSearchBackend>();
#endif
        builder.Services.AddSingleton<FairyAI_Android.Services.Search.IWebSearchBackend, FairyAI_Android.Services.Search.HtmlSearchBackend>();
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Search.CompositeWebSearch(
            sp.GetServices<FairyAI_Android.Services.Search.IWebSearchBackend>()));
        builder.Services.AddSingleton<ITtsApiService, TtsApiService>();
        builder.Services.AddSingleton<ITtsService, TtsService>();
        builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();
        builder.Services.AddSingleton<ILocalSendService, LocalSendService>();
        builder.Services.AddSingleton<WakeWordService>();

        // Notifications and task-summary sync from a paired PC. The sync never starts on its own:
        // SummarySyncService only does anything once the user turns it on in settings.
#if ANDROID
        builder.Services.AddSingleton<INotificationSink, AndroidNotificationSink>();
#else
        builder.Services.AddSingleton<INotificationSink, NullNotificationSink>();
#endif
        builder.Services.AddSingleton<SummarySyncService>();
        builder.Services.AddSingleton<PairedDeskService>();

        // Note sync rides the same paired channel as the task summary, with its own switch and its own
        // watermark file.
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Notes.NoteSyncState(
            Path.Combine(FileSystem.AppDataDirectory, "notes_sync.json")));
        builder.Services.AddSingleton<FairyAI_Android.Services.Notes.NoteSyncClient>();
        builder.Services.AddSingleton(sp => new NoteSyncService(
            sp.GetRequiredService<NoteService>().Store,
            sp.GetRequiredService<FairyAI_Android.Services.Notes.NoteSyncState>(),
            sp.GetRequiredService<FairyAI_Android.Services.Notes.NoteSyncClient>(),
            () =>
            {
                var sync = ConfigManager.Load().Sync;
                return new FairyAI_Android.Services.Notes.NoteSyncSettings(
                    sync.NotesSyncEnabled, sync.SummarySyncEndpoint, sync.DeviceName, sync.SummarySyncToken);
            }));

        // Phone-local screen interaction. One gate, one platform bridge, and an approval dialog that
        // sits between "the user allowed this capability" and "do it now".
        builder.Services.AddSingleton<IScreenApproval, DialogScreenApproval>();
        builder.Services.AddSingleton(sp => new ScreenControl(null, sp.GetRequiredService<IScreenApproval>()));

        // Frame-time sampling behind the dashboard's measurement button.
#if ANDROID
        builder.Services.AddSingleton<IFrameTicker, AndroidFrameTicker>();
#else
        builder.Services.AddSingleton<IFrameTicker, UnsupportedFrameTicker>();
#endif
        builder.Services.AddSingleton(sp => new PerformanceMonitor(sp.GetRequiredService<IFrameTicker>()));

        // Tool calling: the registry is built from real implementations only, and the assistant uses it
        // for every message (falling back to plain streaming when the endpoint refuses tools).
        builder.Services.AddSingleton<TranslateService>();
        builder.Services.AddSingleton<KnowledgeBaseService>();

        // Notes and reminders are phone-local state. One store each, resolved once, so the page, the
        // tools and the reminder loop all see the same in-memory list.
        builder.Services.AddSingleton<NoteService>();
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Reminders.ReminderStore(
            Path.Combine(FileSystem.AppDataDirectory, "reminders.json")));
#if ANDROID
        builder.Services.AddSingleton<FairyAI_Android.Services.Reminders.IReminderAlarm, FairyAI_Android.Services.Reminders.AndroidReminderAlarm>();
#else
        builder.Services.AddSingleton<FairyAI_Android.Services.Reminders.IReminderAlarm,
            FairyAI_Android.Services.Reminders.NoReminderAlarm>();
#endif
        builder.Services.AddSingleton(sp => new ReminderService(
            sp.GetRequiredService<INotificationSink>(),
            sp.GetRequiredService<FairyAI_Android.Services.Reminders.ReminderStore>(),
            sp.GetRequiredService<FairyAI_Android.Services.Reminders.IReminderAlarm>()));

        builder.Services.AddSingleton(sp => ToolCatalog.Create(
            sp.GetRequiredService<ScreenControl>(),
            sp.GetRequiredService<ISearchService>(),
            sp.GetRequiredService<TranslateService>(),
            sp.GetRequiredService<KnowledgeBaseService>(),
            sp.GetRequiredService<NoteService>().Store,
            sp.GetRequiredService<ReminderService>(),
            question => sp.GetRequiredService<VisionService>().LookAsync(question)));
        // Looking at the screen. The capture half is the same gate the screen tools use; the model
        // half reads its endpoint from config on every call, so changing the model in settings works
        // without a restart.
        builder.Services.AddSingleton<ImportantInfoService>();
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Vision.VisionClient(() =>
        {
            var config = ConfigManager.Load();
            var (target, _) = FairyAI_Android.Services.Vision.VisionTargetSelector.Select(
                new FairyAI_Android.Services.Vision.VisionSettings(
                    config.Vision.Provider, config.Vision.BaseUrl, config.Vision.Model, config.Vision.ApiKey)
                {
                    SeesImages = config.Vision.SeesImages == true,
                },
                new FairyAI_Android.Services.Vision.MultimodalSettings(
                    config.CustomPlatform.MultimodalEnabled,
                    config.CustomPlatform.MultimodalBaseUrl,
                    config.CustomPlatform.MultimodalModel,
                    config.CustomPlatform.MultimodalApiKey),
                new FairyAI_Android.Services.Vision.VisionSettings(
                    config.LLM.Provider, config.LLM.BaseUrl, config.LLM.Model, config.LLM.ApiKey)
                {
                    SeesImages = config.LLM.SeesImages == true,
                });
            return target;
        }));
        builder.Services.AddSingleton<VisionService>();

        builder.Services.AddSingleton<FunctionCallingService>();
        builder.Services.AddSingleton<FairyAI_Android.Services.Usage.TokenUsageService>();

        // ViewModels
        builder.Services.AddTransient<MainViewModel>();

        // Pages
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<TasksPage>();
        builder.Services.AddTransient<DevicesPage>();
        builder.Services.AddTransient<NotesPage>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
