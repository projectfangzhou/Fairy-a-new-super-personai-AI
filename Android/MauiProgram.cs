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

#if ANDROID
        // 安卓的 EditText 自带一条下划线。它画在圆角输入框的里面，看起来像把方框切断了；
        // 只把 BackgroundColor 设成 Transparent 去不掉，得直接清掉平台控件的背景。
        builder.ConfigureMauiHandlers(handlers =>
        {
            Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FairyFlatField", (handler, _) => ClearPlatformBackground(handler.PlatformView));
            Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("FairyFlatField", (handler, _) => ClearPlatformBackground(handler.PlatformView));
            Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("FairyFlatField", (handler, _) => ClearPlatformBackground(handler.PlatformView));
        });
#endif

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
            question => sp.GetRequiredService<VisionService>().LookAsync(question),
            sp.GetRequiredService<RAGService>()));
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

        // Answering from the user's own documents. Keyword retrieval is always available; the vector
        // half prefers a model on the phone, falls back to a remote /embeddings endpoint, and says so
        // when neither can run.
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Knowledge.EmbeddingCache(
            Path.Combine(FileSystem.AppDataDirectory, "knowledge", "embeddings.json")));
        builder.Services.AddSingleton<FairyAI_Android.Services.Knowledge.EmbeddingClient>();
        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Knowledge.LocalEmbeddingStore(
            Path.Combine(FileSystem.AppDataDirectory, "knowledge", "models")));

        // The runtime that runs the bundled model on the phone (ONNX Runtime, linked via NuGet).
        builder.Services.AddSingleton<FairyAI_Android.Services.Knowledge.IEmbeddingRuntime>(sp =>
        {
            var store = sp.GetRequiredService<FairyAI_Android.Services.Knowledge.LocalEmbeddingStore>();
            var model = FairyAI_Android.Services.Knowledge.LocalEmbeddingModel.BundledChinese;

            // The model ships inside the APK; the runtime needs a real file path, so it is copied out
            // to app storage once. Failure here is reported through the backend's UnavailableReason.
            _ = Task.Run(async () =>
            {
                try
                {
                    await store.InstallBundledAsync(model, async name =>
                        await FileSystem.OpenAppPackageFileAsync(name));
                }
                catch (Exception)
                {
                    // Reported by the backend's state; nothing to do at start-up.
                }
            });

            return new FairyAI_Android.Services.Knowledge.OnnxEmbeddingRuntime(
                () => store.PathOf(model),
                () => store.VocabPathOf(model));
        });

        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Knowledge.LocalEmbeddingBackend(
            sp.GetRequiredService<FairyAI_Android.Services.Knowledge.LocalEmbeddingStore>(),
            sp.GetRequiredService<FairyAI_Android.Services.Knowledge.IEmbeddingRuntime>(),
            FairyAI_Android.Services.Knowledge.LocalEmbeddingModel.BundledChinese));

        builder.Services.AddSingleton(sp => new FairyAI_Android.Services.Knowledge.RemoteEmbeddingBackend(
            sp.GetRequiredService<FairyAI_Android.Services.Knowledge.EmbeddingClient>(),
            () =>
            {
                var config = ConfigManager.Load();
                var knowledge = config.Knowledge;
                if (!knowledge.VectorSearchEnabled)
                    return new FairyAI_Android.Services.Knowledge.EmbeddingSettings("", "", "");

                return FairyAI_Android.Services.Knowledge.EmbeddingSettings.From(
                    knowledge.EmbeddingsBaseUrl, knowledge.EmbeddingsModel, knowledge.EmbeddingsApiKey,
                    config.LLM.BaseUrl, config.LLM.ApiKey);
            }));

        builder.Services.AddSingleton(sp => new RAGService(
            sp.GetRequiredService<KnowledgeBaseService>(),
            sp.GetRequiredService<ILlmService>(),
            sp.GetRequiredService<FairyAI_Android.Services.Knowledge.EmbeddingCache>(),
            () => FairyAI_Android.Services.Knowledge.EmbeddingBackendSelector.Select(
                sp.GetRequiredService<FairyAI_Android.Services.Knowledge.LocalEmbeddingBackend>(),
                sp.GetRequiredService<FairyAI_Android.Services.Knowledge.RemoteEmbeddingBackend>())));

        builder.Services.AddSingleton<FunctionCallingService>();
        builder.Services.AddSingleton<ContextCompressionService>();
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

#if ANDROID
    /// <summary>清掉平台输入控件的默认背景（那条下划线就在背景里）。</summary>
    private static void ClearPlatformBackground(Android.Views.View? view)
    {
        if (view is null) return;

        view.Background = null;
        view.SetBackgroundColor(Android.Graphics.Color.Transparent);
    }
#endif
}
