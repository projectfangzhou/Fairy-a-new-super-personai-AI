namespace FairyAI_Android.Services.Performance;

/// <summary>
/// Frame-time statistics from a measurement window. Everything is in milliseconds and comes from
/// frames the app actually drew, so "流畅" is a number rather than an opinion.
/// </summary>
public sealed record FrameReport(int Samples, double P50, double P95, double Worst, double JankRatio)
{
    public static readonly FrameReport Empty = new(0, 0, 0, 0, 0);

    public bool HasSamples => Samples > 0;
}

/// <summary>
/// Collects frame intervals and reports percentiles. Pure: no Android, no timer, so the arithmetic
/// that decides "is this smooth" can be asserted directly.
///
/// A frame counts as jank when it takes more than twice the target interval — one missed vsync is
/// normal, two is a visible stutter.
/// </summary>
public sealed class FrameMeter
{
    private readonly double[] _samples;
    private readonly double _targetFrameMs;
    private int _count;
    private int _next;

    public FrameMeter(int capacity = 1200, double targetFrameMs = 1000.0 / 60.0)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (targetFrameMs <= 0) throw new ArgumentOutOfRangeException(nameof(targetFrameMs));

        _samples = new double[capacity];
        _targetFrameMs = targetFrameMs;
    }

    public double TargetFrameMs => _targetFrameMs;

    public int Count => _count;

    /// <summary>Records one frame interval. Nonsense values (≤0, NaN, absurd) are dropped, not clamped.</summary>
    public void Record(double frameMs)
    {
        if (double.IsNaN(frameMs) || double.IsInfinity(frameMs)) return;
        if (frameMs <= 0 || frameMs > 10_000) return;

        _samples[_next] = frameMs;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    public FrameReport Snapshot()
    {
        if (_count == 0) return FrameReport.Empty;

        var sorted = new double[_count];
        Array.Copy(_samples, sorted, _count);
        Array.Sort(sorted);

        var jank = 0;
        for (var i = 0; i < _count; i++)
            if (sorted[i] > _targetFrameMs * 2) jank++;

        return new FrameReport(
            _count,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            sorted[^1],
            (double)jank / _count);
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Linear-interpolated percentile over an already sorted array.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        var clamped = Math.Clamp(fraction, 0, 1);
        var position = clamped * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];

        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }
}
