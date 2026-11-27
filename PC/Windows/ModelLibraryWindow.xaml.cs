using System.Windows;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Models;

namespace MyAiAssistant.Windows;

/// <summary>
/// One place to see every model this machine can reach — files on disk, Ollama, and the cloud
/// presets — and to switch the assistant onto the ones it can actually load.
///
/// The window owns no logic of its own: gathering is a thin shell around the existing services and
/// the decisions all live in <see cref="ModelLibraryCatalog"/>, which is why that part is tested
/// without a UI.
/// </summary>
public partial class ModelLibraryWindow : Window
{
    public ModelLibraryWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModelLibraryEntry entry }) return;
        if (!entry.CanActivate) return;

        switch (entry.Activation)
        {
            case ModelActivation.Ollama:
                OllamaService.ConfigureLocalModel(entry.Name);
                await RefreshAsync();
                StatusText.Text = $"已切换到 {entry.Name}";
                break;
        }
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取…";

        try
        {
            var snapshot = ModelLibraryCatalog.Build(await GatherAsync());

            EntryList.ItemsSource = snapshot.Entries;
            SummaryText.Text = snapshot.Summary;
            ActiveText.Text = snapshot.ActiveLabel;

            var usable = snapshot.Entries.Count(entry => entry.CanActivate);
            StatusText.Text = usable > 0
                ? $"可直接切换 {usable} 个"
                : "没有可直接切换的模型";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取失败：{ex.GetType().Name}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static async Task<ModelLibraryInputs> GatherAsync()
    {
        var config = ConfigManager.Load();

        var ollamaRunning = await OllamaService.IsOllamaRunningAsync();
        IReadOnlyList<string> ollamaModels = [];
        if (ollamaRunning)
            ollamaModels = await OllamaService.ListLocalModelsAsync();

        return new ModelLibraryInputs(
            ReadLocalFiles(),
            ollamaModels,
            ollamaRunning,
            ConfigManager.GetProviderNames(),
            ConfigManager.GetProviderInfo,
            config.LLM.Provider,
            config.LLM.Model);
    }

    private static IReadOnlyList<LocalModel> ReadLocalFiles()
    {
        // The models folder sits next to the exe; an installed copy may not be writable there, and
        // a library that throws over that would be worse than one that lists no files.
        try { return new LocalModelService().ListModels(); }
        catch { return []; }
    }
}
