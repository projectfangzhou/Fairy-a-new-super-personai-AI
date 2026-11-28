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
            OllamaListChecks();
            OllamaFailureChecks();
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

        // --- config points at a model that is no longer pulled ---
        var stale = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [], ["llama3", "qwen2.5"], true, providers, info, "Ollama", "all-minilm:latest"));

        var missing = stale.Entries.Single(e => e.Id == "ollama:missing");
        Assert(missing.Name == "all-minilm:latest", "the stale row names the configured model");
        Assert(missing.BlockedReason == ModelLibraryCatalog.OllamaMissingReason,
            "the stale row says what is wrong");
        Assert(!missing.CanActivate, "the stale row offers no switch");
        Assert(stale.Entries.Count(e => e.IsActive) == 0, "no row claims the active marker for a missing model");
        Assert(stale.Entries.Count(e => e.Source == ModelSource.Ollama) == 3,
            "pulled models stay listed alongside the stale row");
        Assert(stale.ActiveLabel == "Ollama / all-minilm:latest",
            "the active label still tells the truth about what is configured");

        // --- the summary must count what is actually listed ---
        var withBlank = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [
                new LocalModel { Name = "real", Format = "gguf", Quantization = "Q4", SizeBytes = 1024 },
                new LocalModel { Name = "   ", Format = "gguf" }
            ],
            ["llama3"], true, providers, info, "Ollama", "llama3"));

        var listedFiles = withBlank.Entries.Count(e => e.Source == ModelSource.LocalFile);
        Assert(listedFiles == 1, "a nameless file is not listed");
        Assert(withBlank.Summary.Contains($"本机文件 {listedFiles} 个", StringComparison.Ordinal),
            "the summary counts the rows it actually shows: " + withBlank.Summary);

        // --- formatting ---
        Assert(ModelLibraryCatalog.FormatSize(0) == "0 B", "zero size");
        Assert(ModelLibraryCatalog.FormatSize(512) == "512 B", "bytes");
        Assert(ModelLibraryCatalog.FormatSize(1536) == "2 KB", "kilobytes round");
        Assert(ModelLibraryCatalog.FormatSize(4_400_000_000) == "4.1 GB", "gigabytes keep one decimal");
    }

    private static void OllamaListChecks()
    {
        // The samples below are the real shapes this machine produced during verification.

        // 1. Fresh install: header only.
        var empty = OllamaListParser.Parse("NAME    ID    SIZE    MODIFIED \n");
        Assert(empty.Count == 0, "header only parses to nothing");

        // 2. One model, padded columns.
        var single = OllamaListParser.Parse(
            "NAME                 ID              SIZE     MODIFIED               \n" +
            "all-minilm:latest    1b226e2802db    45 MB    Less than a second ago    \n");
        Assert(single.Count == 1 && single[0] == "all-minilm:latest", "one real row parses to one model");

        // 3. Two models with different sizes and timestamps.
        var two = OllamaListParser.Parse(
            "NAME                 ID              SIZE      MODIFIED      \n" +
            "llava-phi3:latest    c7edd7b87593    2.9 GB    3 weeks ago   \n" +
            "gemma2:2b            8ccf136fdd52    1.6 GB    3 months ago  \n");
        Assert(two.Count == 2, "two real rows parse to two models");
        Assert(two[0] == "llava-phi3:latest" && two[1] == "gemma2:2b", "order and spelling are preserved");

        // 4. Windows line endings must not leak a carriage return into the name.
        var crlf = OllamaListParser.Parse("NAME  ID  SIZE  MODIFIED\r\nx:latest  aaa  1 GB  now\r\n");
        Assert(crlf.Count == 1 && crlf[0] == "x:latest", "crlf input parses cleanly");

        // 5. A warning printed before the table must not become a model, and must not eat a row.
        var warned = OllamaListParser.Parse(
            "Warning: could not connect to a running Ollama instance\n" +
            "NAME                 ID              SIZE      MODIFIED   \n" +
            "gemma2:2b            8ccf136fdd52    1.6 GB    3 months ago\n");
        Assert(warned.Count == 1 && warned[0] == "gemma2:2b", "a warning line is ignored, the row survives");

        // 6. Prose instead of a table (daemon down, unknown command) yields no models at all.
        var prose = OllamaListParser.Parse("Error: could not connect to ollama server\n");
        Assert(prose.Count == 0, "an error message produces no model");
        Assert(OllamaListParser.Parse("").Count == 0, "empty output produces no model");
        Assert(OllamaListParser.Parse(null).Count == 0, "null output produces no model");

        // 7. Control codes from a terminal (spinners) must be stripped.
        var ansi = OllamaListParser.Parse(
            "\u001B[?25lNAME   ID   SIZE   MODIFIED\n\u001B[Kqwen2.5:3b   abc   1.9 GB   now\u001B[?25h\n");
        Assert(ansi.Count == 1 && ansi[0] == "qwen2.5:3b", "ansi control codes are stripped");

        // 8. Duplicates collapse instead of listing the same model twice.
        var dupes = OllamaListParser.Parse("NAME  ID  SIZE  M\na:1  x  1 MB  now\na:1  x  1 MB  now\n");
        Assert(dupes.Count == 1, "a repeated row is listed once");
    }

    private static void OllamaFailureChecks()
    {
        // An empty list and a broken command both produce zero models. They must not be reported
        // the same way, or the model library tells the user their models are gone.
        var ok = OllamaListParser.Interpret(0, "qwen2.5:3b  abc  1 MB  now\n", "");
        Assert(ok.Error is null && ok.Models.Count == 1, "success carries the models and no error");

        var emptyButFine = OllamaListParser.Interpret(0, "", "");
        Assert(emptyButFine.Error is null && emptyButFine.Models.Count == 0,
            "an empty list with a clean exit is not an error");

        var failed = OllamaListParser.Interpret(1, "", "Error: could not connect to ollama server\n");
        Assert(failed.Error is not null, "a failed command carries an error");
        Assert(failed.Error!.Contains("ollama server", StringComparison.Ordinal),
            "the error carries the CLI's own words: " + failed.Error);

        var silentFailure = OllamaListParser.Interpret(2, "", "");
        Assert(silentFailure.Error is not null && silentFailure.Error!.Contains("2", StringComparison.Ordinal),
            "an unexplained failure still names the exit code");

        // The catalogue must turn that error into a row, not into "you have no models".
        var providers = new List<string> { "Kimi", "Ollama" };
        Func<string, (string BaseUrl, string Model)?> info = name =>
            name == "Ollama" ? ("http://localhost:11434/v1/chat/completions", "gemma2:2b") : ("", "");

        var unreadable = ModelLibraryCatalog.Build(new ModelLibraryInputs(
            [], [], true, providers, info, "Kimi", "kimi", "ollama list 失败（退出码 1）：Error: could not connect"));

        var row = unreadable.Entries.Single(e => e.Id == "ollama:unreadable");
        Assert(row.BlockedReason == ModelLibraryCatalog.OllamaListErrorReason, "the row says reading failed");
        Assert(row.Detail.Contains("退出码 1", StringComparison.Ordinal), "the row carries the command's own reason");
        Assert(!row.CanActivate, "an unreadable group offers nothing to switch to");
        Assert(!unreadable.Entries.Any(e => e.Id == "ollama:empty"),
            "a read failure is not reported as an empty library");
        Assert(unreadable.Summary.Contains("Ollama 0 个", StringComparison.Ordinal),
            "the summary still counts zero, because zero is what we know");
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
