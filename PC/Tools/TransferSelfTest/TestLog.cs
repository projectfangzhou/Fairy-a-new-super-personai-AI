using System.IO;
using System.Text.Json;

namespace TransferSelfTest;

/// <summary>Step log plus a machine-readable summary, because a two-machine run cannot be watched.</summary>
internal sealed class TestLog
{
    private static readonly object Sync = new();
    private readonly List<string> _failures = [];
    private readonly List<LogStep> _steps = [];
    private readonly string _role;

    public TestLog(string role, string root)
    {
        _role = role;
        LogPath = Path.Combine(root, "selftest-" + role + ".log");
        SummaryPath = Path.Combine(root, "selftest-" + role + ".json");

        Write("==== 仙灵跨设备传输自检 · " + role + " ====");
        Write($"机器 {Environment.MachineName} · 系统 {Environment.OSVersion} · 目录 {root}");
    }

    public string LogPath { get; }

    public string SummaryPath { get; }

    public bool Passed => _failures.Count == 0;

    public void Step(string message)
    {
        _steps.Add(new LogStep(DateTimeOffset.UtcNow, "step", message));
        Write("-- " + message);
    }

    public void Info(string message)
    {
        _steps.Add(new LogStep(DateTimeOffset.UtcNow, "info", message));
        Write("   " + message);
    }

    public void Ok(string message)
    {
        _steps.Add(new LogStep(DateTimeOffset.UtcNow, "ok", message));
        Write("OK " + message);
    }

    public void Fail(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}\n{exception}";
        _failures.Add(detail);
        _steps.Add(new LogStep(DateTimeOffset.UtcNow, "fail", message));
        Write("!! " + message);
        if (exception is not null) Write(exception.ToString());
    }

    public void Finish() =>
        Write(Passed ? "==== 结果：PASS ====" : $"==== 结果：FAIL（{_failures.Count} 处）====");

    public void WriteSummary()
    {
        var payload = new
        {
            role = _role,
            machine = Environment.MachineName,
            startedAt = _steps.FirstOrDefault()?.At,
            finishedAt = DateTimeOffset.UtcNow,
            passed = Passed,
            failures = _failures,
            steps = _steps.Select(step => new { at = step.At, kind = step.Kind, text = step.Text })
        };

        try
        {
            File.WriteAllText(SummaryPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // The log itself is the fallback; the summary is a convenience.
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void Write(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        Console.WriteLine(text);

        lock (Sync)
        {
            try
            {
                File.AppendAllText(LogPath, text + Environment.NewLine);
            }
            catch (IOException)
            {
                // Nothing useful to do if even the log cannot be written.
            }
        }
    }

    private sealed record LogStep(DateTimeOffset At, string Kind, string Text);
}
