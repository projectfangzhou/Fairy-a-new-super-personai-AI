using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// Locates an engine runtime. The seam exists so callers (and tests) can decide availability
/// without touching the file system or launching processes.
/// </summary>
public interface IEngineLocator
{
    Task<EngineAvailability> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>The pinned Pi Agent copy that ships with the app.</summary>
public static class PiAgentVendored
{
    public const string ExpectedVersion = "0.84.3";
    public const string FolderName = "PiAgent";
    public const string EntryFile = @"dist\bundle\cli.js";
    public const string ManifestFile = "manifest.json";

    /// <summary>
    /// Checks the vendored copy against its manifest. Returns the mismatches rather than
    /// throwing: the caller decides whether an unverifiable runtime may be used.
    /// </summary>
    public static IReadOnlyList<string> Verify(string cliPath)
    {
        var problems = new List<string>();
        var root = FindManifestRoot(cliPath);
        if (root is null)
            return ["缺少 manifest.json，无法校验内置副本"];

        var manifestPath = Path.Combine(root, ManifestFile);
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
        catch (JsonException) { return ["manifest.json 不是合法 JSON"]; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return ["manifest.json 缺少 files 列表"];

            var expectedVersion = document.RootElement.TryGetProperty("version", out var version) &&
                                  version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
            if (expectedVersion != ExpectedVersion)
                problems.Add($"内置副本版本为 {expectedVersion ?? "未知"}，代码期望 {ExpectedVersion}");

            foreach (var entry in files.EnumerateArray())
            {
                var relative = entry.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                    ? path.GetString()
                    : null;
                var expected = entry.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String
                    ? hash.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expected))
                {
                    problems.Add("manifest.json 中有条目缺少 path 或 sha256");
                    continue;
                }

                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    problems.Add($"缺少文件：{relative}");
                    continue;
                }

                using var stream = File.OpenRead(full);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"校验失败：{relative}");
            }
        }

        return problems;
    }

    /// <summary>The manifest sits at the package root, above <c>dist/bundle/cli.js</c>.</summary>
    private static string? FindManifestRoot(string cliPath)
    {
        var dir = Path.GetDirectoryName(cliPath);
        for (var depth = 0; depth < 5 && !string.IsNullOrEmpty(dir); depth++)
        {
            if (File.Exists(Path.Combine(dir, ManifestFile))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

public enum EngineSource
{
    Unknown = 0,
    Vendored,
    Environment,
    GlobalInstall
}

/// <summary>
/// Finds the Pi Agent runtime the way the rest of the app treats optional engines: look for
/// real files, actually run the CLI to prove it works, and report the exact missing piece
/// when it does not. No "installed" claim is made from the presence of a shim alone.
/// </summary>
public sealed class PiAgentLocator : IEngineLocator
{
    private const string PackagePath = @"node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js";
    private const string VendoredRelative = @"ThirdParty\PiAgent\dist\bundle\cli.js";

    private readonly string? _nodeOverride;
    private readonly string? _cliOverride;

    public PiAgentLocator(string? nodeOverride = null, string? cliOverride = null)
    {
        _nodeOverride = nodeOverride ?? Environment.GetEnvironmentVariable("FAIRY_NODE");
        _cliOverride = cliOverride ?? Environment.GetEnvironmentVariable("FAIRY_PI_CLI");
    }

    public EngineAvailability Locate()
    {
        var node = FindNode();
        if (node is null)
            return EngineAvailability.Unusable(EngineAvailabilityState.NodeMissing,
                "未找到 Node.js 运行时：Fairy Code 需要本机 Node.js（≥ 22.19）才能启动 Pi 主循环");

        var (cli, source) = FindCliWithSource();
        if (cli is null)
            return EngineAvailability.Unusable(EngineAvailabilityState.CliMissing,
                "未找到 Pi Agent CLI：既没有随应用分发的那一份，本机也没有全局安装；" +
                "可用 FAIRY_PI_CLI 指定 cli.js 路径");

        return new EngineAvailability(
            EngineAvailabilityState.Ready, "Pi Agent 可用", node, cli, "unknown",
            SourceText(source));
    }

    /// <summary>Runs <c>node cli.js --version</c> so a broken install is reported honestly.</summary>
    public async Task<EngineAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var located = Locate();
        if (!located.IsReady) return located;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = located.NodePath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(located.CliPath!);
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
                return EngineAvailability.Unusable(EngineAvailabilityState.ProbeFailed,
                    "无法启动 Pi Agent 进程");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return EngineAvailability.Unusable(EngineAvailabilityState.ProbeFailed,
                    $"Pi Agent 探测失败（退出码 {process.ExitCode}）：{Trim(stdout)}");

            return located with { Version = stdout.Trim() };
        }
        catch (OperationCanceledException)
        {
            return EngineAvailability.Unusable(EngineAvailabilityState.ProbeFailed, "Pi Agent 探测超时");
        }
        catch (Exception ex)
        {
            return EngineAvailability.Unusable(EngineAvailabilityState.ProbeFailed,
                $"Pi Agent 探测失败：{ex.GetType().Name}");
        }
    }

    internal string? FindNode()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_nodeOverride)) candidates.Add(_nodeOverride);
        candidates.AddRange(FromPath("node.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    internal string? FindCli() => FindCliWithSource().Path;

    internal (string? Path, EngineSource Source) FindCliWithSource()
    {
        if (!string.IsNullOrWhiteSpace(_cliOverride) && File.Exists(_cliOverride))
            return (_cliOverride, EngineSource.Environment);

        // 1. The copy shipped next to the executable.
        var shipped = Path.Combine(AppContext.BaseDirectory, VendoredRelative);
        if (File.Exists(shipped)) return (shipped, EngineSource.Vendored);

        // 2. The same copy inside the repository, for `dotnet run` during development.
        var repositoryCopy = FindUpwards(AppContext.BaseDirectory, VendoredRelative);
        if (repositoryCopy is not null) return (repositoryCopy, EngineSource.Vendored);

        // 3. Whatever is installed globally on this machine.
        var candidates = new List<string>();
        foreach (var dir in FromPath("pi.cmd").Select(Path.GetDirectoryName).OfType<string>())
            candidates.Add(Path.Combine(dir, PackagePath));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            candidates.Add(Path.Combine(appData, "npm", PackagePath));

        candidates.Add(Path.Combine(@"D:\npm-global", PackagePath));
        candidates.Add(Path.Combine(@"C:\npm-global", PackagePath));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", PackagePath));

        var global = candidates.FirstOrDefault(File.Exists);
        return (global, global is null ? EngineSource.Unknown : EngineSource.GlobalInstall);
    }

    private static string? FindUpwards(string start, string relative)
    {
        var dir = new DirectoryInfo(start);
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    internal static string SourceText(EngineSource source) => source switch
    {
        EngineSource.Vendored => "内置副本",
        EngineSource.Environment => "环境变量指定",
        EngineSource.GlobalInstall => "本机全局安装",
        _ => "未知来源"
    };

    private static IEnumerable<string> FromPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(raw.Trim('"'), fileName); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }
    }

    private static string Trim(string value)
    {
        var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }
}
