namespace FairyAI_Android.Services.Performance;

/// <summary>
/// Somewhere that reports how long each drawn frame took. Android has Choreographer; other platforms
/// have nothing here yet, and say so.
/// </summary>
public interface IFrameTicker
{
    bool IsSupported { get; }

    /// <summary>Begins reporting frame intervals to <paramref name="onFrame"/>. Call from the UI thread.</summary>
    bool Start(Action<double> onFrame);

    void Stop();
}

public sealed class UnsupportedFrameTicker : IFrameTicker
{
    public bool IsSupported => false;

    public bool Start(Action<double> onFrame) => false;

    public void Stop()
    {
    }
}

/// <summary>
/// Runs a bounded measurement window and keeps the result. Bounded on purpose: a permanent
/// per-frame callback would itself cost battery, and a three-second sample answers the question.
/// </summary>
public sealed class PerformanceMonitor
{
    private readonly FrameMeter _meter = new();
    private readonly IFrameTicker _ticker;

    public PerformanceMonitor(IFrameTicker? ticker = null) => _ticker = ticker ?? new UnsupportedFrameTicker();

    public bool IsSupported => _ticker.IsSupported;

    public bool IsSampling { get; private set; }

    public FrameReport LastReport { get; private set; } = FrameReport.Empty;

    public int CollectionsGen0 { get; private set; }

    public int CollectionsGen2 { get; private set; }

    public long ManagedHeapBytes { get; private set; }

    public double TargetFrameMs => _meter.TargetFrameMs;

    /// <summary>Measures for <paramref name="duration"/>, then stops and stores the report.</summary>
    public async Task<(bool Ok, string Message)> SampleAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (!_ticker.IsSupported)
            return (false, "这台设备上还没有帧时间采样通道（目前只有 Android 有）。");

        if (IsSampling)
            return (false, "已经在测了，等这一次结束。");

        IsSampling = true;
        _meter.Reset();
        CollectionsGen0 = GC.CollectionCount(0);
        CollectionsGen2 = GC.CollectionCount(2);

        var started = false;
        try
        {
            started = _ticker.Start(_meter.Record);
            if (!started) return (false, "帧时间采样没能启动。");

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, "测量被取消。");
        }
        finally
        {
            if (started) _ticker.Stop();

            CollectionsGen0 = GC.CollectionCount(0) - CollectionsGen0;
            CollectionsGen2 = GC.CollectionCount(2) - CollectionsGen2;
            ManagedHeapBytes = GC.GetTotalMemory(false);
            LastReport = _meter.Snapshot();
            IsSampling = false;
        }

        if (!LastReport.HasSamples)
            return (false, "这次没有采到帧，可能界面完全静止。保持页面在前台再试一次。");

        return (true, Describe(LastReport));
    }

    public string Describe(FrameReport report)
    {
        if (!report.HasSamples) return "还没有测量数据";

        return $"帧时间 p50 {report.P50:F1}ms · p95 {report.P95:F1}ms · 最差 {report.Worst:F1}ms · "
               + $"掉帧 {report.JankRatio * 100:F0}%（{report.Samples} 帧，目标 {TargetFrameMs:F1}ms）";
    }

    public string DescribeMemory() =>
        $"托管堆 {ManagedHeapBytes / 1024.0 / 1024.0:F1}MB · 本次测量期间 GC 0 代 {CollectionsGen0} 次、2 代 {CollectionsGen2} 次";
}
