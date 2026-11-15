using FairyAI_Android.Services.Notes;

namespace FairyAI_Android.Services;

/// <summary>
/// Keeps this phone's notes and the paired computer's notes in step.
///
/// The model is deliberately simple, because a clever one that silently loses edits is worse than a
/// plain one that can be explained: last writer wins by <c>UpdatedAt</c>, deletions travel as
/// tombstones, and the watermark only limits how much is sent — it is never trusted to decide what is
/// newer. Two rules follow from that: a failed round changes nothing locally, and a round that
/// half-applied reports the failure instead of claiming success.
/// </summary>
public sealed class NoteSyncService : IDisposable
{
    /// <summary>Tombstones older than this are dropped; a device offline for longer gets a full re-send.</summary>
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(90);

    private readonly NoteStore _store;
    private readonly NoteSyncState _state;
    private readonly NoteSyncClient _client;
    private readonly Func<NoteSyncSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public NoteSyncService(NoteStore store, NoteSyncState state, NoteSyncClient client, Func<NoteSyncSettings> settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    public bool IsRunning => _cts is not null;

    /// <summary>Completed rounds.</summary>
    public int Rounds { get; private set; }

    /// <summary>Notes this phone sent, over all rounds.</summary>
    public int Pushed { get; private set; }

    /// <summary>Notes this phone took from the computer, over all rounds.</summary>
    public int Pulled { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public string? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        await SyncOnceAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not an error.
        }
    }

    public async Task<NoteSyncOutcome> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var sync = _settings();

        if (!sync.Enabled)
            return NoteSyncOutcome.Fail("笔记同步未开启：在「看板 → 设备连接」里打开开关");

        if (string.IsNullOrWhiteSpace(sync.Endpoint) || string.IsNullOrWhiteSpace(sync.Token))
            return NoteSyncOutcome.Fail("还没有和电脑配对，或电脑没有下发令牌");

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return NoteSyncOutcome.Fail("上一轮同步还没有结束");

        try
        {
            _store.PruneTombstones(DateTimeOffset.Now - TombstoneLifetime);

            // Two watermarks, two time domains: `since` is the computer's, what to push is measured
            // on this phone's clock.
            var since = _state.LastSyncAt;
            var changed = _store.ChangedSince(_state.LastPushedAt);
            var pushedUpTo = changed.Count == 0 ? _state.LastPushedAt : changed.Max(note => note.UpdatedAt.ToUnixTimeMilliseconds());

            var (ok, message, response) = await _client
                .SyncAsync(sync.Endpoint, sync.DeviceName, sync.Token, since, changed, cancellationToken)
                .ConfigureAwait(false);

            if (!ok || response is null)
            {
                LastError = message;
                return NoteSyncOutcome.Fail(message);
            }

            var incoming = response.Notes.Select(NoteSyncWire.FromWire).ToList();
            var (applied, applyError) = _store.ApplyRemoteBatch(incoming);

            if (applyError is not null)
            {
                LastError = applyError;
                return NoteSyncOutcome.Fail(applyError);
            }

            _state.Record(response.ServerTime, pushedUpTo);

            Rounds++;
            Pushed += changed.Count;
            Pulled += applied;
            LastSuccessAt = DateTimeOffset.Now;
            LastError = null;

            Changed?.Invoke(this, EventArgs.Empty);

            return new NoteSyncOutcome(
                true,
                $"同步完成：上传 {changed.Count} 条，接收 {applied} 条",
                changed.Count,
                applied,
                response.ServerTime);
        }
        catch (OperationCanceledException)
        {
            return NoteSyncOutcome.Fail("这一轮同步被取消了");
        }
        finally
        {
            _gate.Release();
        }
    }

    public string Describe()
    {
        var rows = $"本机 {_store.Count} 条笔记";
        if (_store.TombstoneCount > 0) rows += $"（{_store.TombstoneCount} 条待同步删除）";

        if (LastSuccessAt is { } at)
            return $"{rows} · 上次同步 {at:MM-dd HH:mm}";

        return LastError is { Length: > 0 } ? $"{rows} · 还没有成功同步过：{LastError}" : $"{rows} · 还没有同步过";
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}
