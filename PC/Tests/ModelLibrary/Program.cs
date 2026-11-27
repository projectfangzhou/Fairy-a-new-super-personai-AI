using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Models;
using MyAiAssistant.Services.Theme;
using MyAiAssistant.Windows;

namespace ModelLibrary.Tests;

/// <summary>
/// Checks the catalogue as pure logic, then loads the real window on an STA thread so the XAML is
/// exercised rather than merely compiled. Pass an output path as the first argument to also render
/// the window to a PNG — that is the only way to see the layout without a human at the screen.
/// </summary>
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            CatalogueChecks();
            WindowChecks(args.Length > 0 ? args[0] : null);
            Console.WriteLine($"All {_checks} model library checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static void CatalogueChecks()
    {
        var localFiles = new List<LocalModel>
        {
            new() { Name = "qwen2.5-7b", Path = "a.gguf", SizeBytes = 4_400_000_000, Format = "gguf", Quantization = "Q4" },
            new() { Name = "llava-phi3", Path = "b.gguf", SizeBytes = 2_600_000_000, Format = "gguf", Quantization = "Unknown" }
        };

        var providers = new List<string> { "Kimi", "Ollama", "自定义平台" };
        Func<string, (string BaseUrl, string Model)?> info = name => name switch
        {
            "Kimi" => ("https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6"),
            "Ollama" => ("http://localhost:11434/v1/chat/completions", "llama3"),
            _ => ("", "custom")
        };

        // --- daemon up, and the configured model is one of the pulled ones ---
        var live = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            localFiles, ["llama3", "qwen2.5"], true, providers, info, "Ollama", "llama3"));

        Assert(live.ActiveLabel == "Ollama / llama3", "active label names both provider and model");

        var ollama = live.Entries.Where(e => e.Source == ModelSource.Ollama).ToArray();
        Assert(ollama.Length == 2, "pulled ollama models are listed");
        Assert(ollama.All(e => e.CanActivate), "ollama rows are switchable");
        Assert(ollama.All(e => e.Activation == ModelActivation.Ollama), "ollama rows activate through ollama");
        Assert(ollama.Single(e => e.IsActive).Name == "llama3", "the model in config is the active one");
        Assert(live.Entries.Count(e => e.IsActive) == 1, "exactly one row is ever active");

        Assert(!live.Entries.Any(e => e.Source == ModelSource.CloudProvider && e.Name == "Ollama"),
            "ollama is not repeated as a cloud preset");
        Assert(live.Entries[0].Source == ModelSource.Ollama, "the switchable group comes first");

        var files = live.Entries.Where(e => e.Source == ModelSource.LocalFile).ToArray();
        Assert(files.Length == 2, "local files are listed");
        Assert(files.All(e => !e.CanActivate), "local files offer no switch");
        Assert(files.All(e => e.BlockedReason == ModelLibraryCatalog.LocalFileReason),
            "local files say why they cannot be switched");
        Assert(files.Single(f => f.Name == "qwen2.5-7b").Detail.Contains("4.1 GB", StringComparison.Ordinal),
            "file size is human readable");
        Assert(files.Single(f => f.Name == "qwen2.5-7b").Detail.Contains("Q4", StringComparison.Ordinal),
            "quantisation is shown when known");
        Assert(files.Single(f => f.Name == "llava-phi3").Detail.Contains("gguf", StringComparison.Ordinal),
            "an unknown quantisation falls back to the format");

        var cloud = live.Entries.Where(e => e.Source == ModelSource.CloudProvider).ToArray();
        Assert(cloud.Length == 2, "the cloud group holds the remaining providers");
        Assert(cloud.All(e => !e.CanActivate), "cloud presets are not switchable from here");
        Assert(cloud.Single(c => c.Name == "Kimi").Detail.Contains("moonshot", StringComparison.Ordinal),
            "a cloud row shows its endpoint");
        Assert(cloud.Single(c => c.Name == "自定义平台").Detail == ModelLibraryCatalog.CustomProviderDetail,
            "a provider without an endpoint says so");
        Assert(cloud.Single(c => c.Name == "Kimi").BlockedReason == ModelLibraryCatalog.CloudReason,
            "cloud rows explain where to switch");

        Assert(live.Summary.Contains("本机文件 2 个", StringComparison.Ordinal), "summary counts files");
        Assert(live.Summary.Contains("Ollama 2 个", StringComparison.Ordinal), "summary counts ollama");
        Assert(live.Summary.Contains("云端 2 个", StringComparison.Ordinal), "summary leaves ollama out of cloud");

        // --- daemon down ---
        var down = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [], [], false, providers, info, "Kimi", "kimi-k2.6"));

        var downOllama = down.Entries.Where(e => e.Source == ModelSource.Ollama).ToArray();
        Assert(downOllama.Length == 1, "a stopped daemon still explains itself");
        Assert(!downOllama[0].CanActivate, "nothing is switchable while the daemon is down");
        Assert(downOllama[0].BlockedReason == ModelLibraryCatalog.OllamaStoppedReason,
            "the placeholder names the daemon");
        Assert(down.Summary.Contains("Ollama 0 个", StringComparison.Ordinal), "no models counted while down");

        var activeRow = down.Entries.Single(e => e.IsActive);
        Assert(activeRow.Name == "Kimi", "the configured provider is the active row");
        Assert(activeRow.BlockedReason is null, "the active row is not blocked as switchable");
        Assert(down.Entries.Count(e => e.IsActive) == 1, "still exactly one active row");

        // --- daemon up but nothing pulled (fresh install) ---
        var fresh = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [], [], true, providers, info, "Kimi", "kimi-k2.6"));

        var freshOllama = fresh.Entries.Where(e => e.Source == ModelSource.Ollama).ToArray();
        Assert(freshOllama.Length == 1, "a live daemon with nothing pulled still explains itself");
        Assert(!freshOllama[0].CanActivate, "a fresh install offers nothing to switch");
        Assert(freshOllama[0].BlockedReason == ModelLibraryCatalog.OllamaEmptyReason,
            "the placeholder names the empty list");
        Assert(fresh.Summary.Contains("Ollama 0 个", StringComparison.Ordinal),
            "no models counted in a fresh install");

        // --- formatting ---
        Assert(ModelLibraryCatalog.FormatSize(0) == "0 B", "zero size");
        Assert(ModelLibraryCatalog.FormatSize(512) == "512 B", "bytes");
        Assert(ModelLibraryCatalog.FormatSize(1536) == "2 KB", "kilobytes round");
        Assert(ModelLibraryCatalog.FormatSize(4_400_000_000) == "4.1 GB", "gigabytes keep one decimal");
    }

    private static void WindowChecks(string? shotPath)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Match the real app: it merges the control baseline before the theme dictionary, so the
        // render has to do the same or it would be judging a window nobody ever sees.
        Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyAiAssistant;component/Themes/Controls.xaml",
                UriKind.Absolute)
        });

        ThemeManager.Apply(FairyTheme.MonoDark);

        var window = new ModelLibraryWindow();

        Assert(window.FindName("EntryList") is ListBox, "the entry list loads from XAML");
        Assert(window.FindName("SummaryText") is TextBlock, "the summary loads from XAML");
        Assert(window.FindName("ActiveText") is TextBlock, "the active label loads from XAML");
        Assert(window.FindName("RefreshButton") is Button, "the refresh button loads from XAML");
        Assert(window.FindName("StatusText") is TextBlock, "the status line loads from XAML");

        if (shotPath is not null)
        {
            Render(window, shotPath);
            Assert(File.Exists(shotPath), "the window rendered to a file");
        }

        window.Close();
    }

    private static void Render(ModelLibraryWindow window, string path)
    {
        if (window.FindName("EntryList") is ListBox list) list.ItemsSource = SampleEntries();
        if (window.FindName("SummaryText") is TextBlock summary) summary.Text = "本机文件 2 个 · Ollama 2 个 · 云端 2 个";
        if (window.FindName("ActiveText") is TextBlock active) active.Text = "Ollama / llama3";
        if (window.FindName("StatusText") is TextBlock status) status.Text = "可直接切换 2 个";

        if (window.Content is not FrameworkElement content) throw new Exception("window has no content");

        // The window itself cannot be rendered without a window handle, so the content is hosted
        // in an opaque border of the same size and that is what gets rasterised. Detach it first:
        // an element cannot have two logical parents.
        window.Content = null;

        var host = new Border
        {
            Child = content,
            Width = 780,
            Height = 680,
            Background = ThemeManager.GetBrush("BgBrush", Brushes.Black)
        };

        window.Content = host;

        host.Measure(new Size(780, 680));
        host.Arrange(new Rect(0, 0, 780, 680));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(780, 680, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IReadOnlyList<ModelLibraryEntry> SampleEntries() =>
        ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [
                new LocalModel { Name = "qwen2.5-7b", Format = "gguf", Quantization = "Q4", SizeBytes = 4_400_000_000 },
                new LocalModel { Name = "llava-phi3", Format = "gguf", Quantization = "Unknown", SizeBytes = 2_600_000_000 }
            ],
            ["llama3", "qwen2.5"],
            true,
            ["Kimi", "Ollama", "DeepSeek", "自定义平台"],
            name => name switch
            {
                "Kimi" => ("https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6"),
                "Ollama" => ("http://localhost:11434/v1/chat/completions", "llama3"),
                "DeepSeek" => ("https://api.deepseek.com/v1/chat/completions", "deepseek-chat"),
                _ => ("", "custom")
            },
            "Ollama",
            "llama3")).Entries;

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
}
