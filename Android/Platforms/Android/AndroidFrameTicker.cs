#if ANDROID
using Android.Views;

namespace FairyAI_Android.Services.Performance;

/// <summary>
/// Frame intervals from Android's own Choreographer — the display's clock, so this measures what the
/// user sees rather than what the app believes it drew.
///
/// Choreographer is looper-bound, so start/stop hop to the UI thread themselves instead of trusting
/// the caller: the measurement continues after an await, and a continuation may land on any thread.
/// Each frame re-posts itself; stopping clears the callback first so reposting cannot outlive it.
/// </summary>
public sealed class AndroidFrameTicker : IFrameTicker
{
    private Callback? _callback;
    private long _previousNanos;

    public bool IsSupported => true;

    public bool Start(Action<double> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        try
        {
            // Runs inline when already on the UI thread; otherwise waits for it.
            return MainThread.InvokeOnMainThreadAsync(() => StartOnMainThread(onFrame))
                .GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            MainThread.InvokeOnMainThreadAsync(StopOnMainThread).GetAwaiter().GetResult();
        }
        catch
        {
            // Nothing sensible to do here; the sampler already gives up on its own.
        }
    }

    private bool StartOnMainThread(Action<double> onFrame)
    {
        StopOnMainThread();

        var choreographer = Choreographer.Instance;
        if (choreographer is null) return false;

        _previousNanos = 0;
        _callback = new Callback(nanos =>
        {
            if (_previousNanos != 0) onFrame((nanos - _previousNanos) / 1_000_000.0);
            _previousNanos = nanos;

            var current = _callback;
            if (current is not null) Choreographer.Instance?.PostFrameCallback(current);
        });

        choreographer.PostFrameCallback(_callback);
        return true;
    }

    private void StopOnMainThread()
    {
        var callback = _callback;
        _callback = null;
        _previousNanos = 0;

        if (callback is not null) Choreographer.Instance?.RemoveFrameCallback(callback);
    }

    private sealed class Callback : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly Action<long> _onFrame;

        public Callback(Action<long> onFrame) => _onFrame = onFrame;

        public void DoFrame(long frameTimeNanos) => _onFrame(frameTimeNanos);
    }
}
#endif
