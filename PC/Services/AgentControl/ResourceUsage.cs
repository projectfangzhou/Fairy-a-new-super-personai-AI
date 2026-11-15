using System.Diagnostics;

namespace MyAiAssistant.Services.AgentControl;

public sealed record ResourceUsageSnapshot(
    DateTimeOffset At,
    long WorkingSetBytes,
    long PrivateBytes,
    int ThreadCount,
    TimeSpan Uptime);

/// <summary>
/// Cheap on-demand sampling of this process. Deliberately local-only: nothing here is
/// ever transmitted, matching the project rule that hardware detail stays on device.
/// </summary>
public static class ResourceUsage
{
    public static ResourceUsageSnapshot Sample()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var now = DateTimeOffset.UtcNow;
        TimeSpan uptime;
        try { uptime = now - process.StartTime.ToUniversalTime(); }
        catch (Exception) { uptime = TimeSpan.Zero; }

        return new ResourceUsageSnapshot(
            now, process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count, uptime);
    }
}