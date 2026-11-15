namespace MyAiAssistant.Services.Transfer;

public sealed record SignalDelivery(long Id, string From, string Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// Where signals wait while the other device is between polls.
///
/// It holds only sealed blobs, so a mailbox that outlives its purpose still contains nothing
/// readable, and everything expires quickly: signalling that arrives after the connection attempt
/// has ended is useless anyway, and keeping it would only widen what a later compromise exposes.
/// </summary>
public sealed class SignalMailbox
{
    public const int MaxPerRecipient = 64;
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _inboxes = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private long _nextId = 1;

    public SignalMailbox(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public long Post(string groupId, string to, string from, string payload)
    {
        var now = _clock();

        lock (_gate)
        {
            var key = groupId + "|" + to;
            if (!_inboxes.TryGetValue(key, out var inbox))
            {
                inbox = [];
                _inboxes[key] = inbox;
            }

            Prune(inbox, now);

            var id = _nextId++;
            inbox.Add(new Entry(id, from, payload, now));

            // A peer that never polls must not turn into unbounded memory.
            while (inbox.Count > MaxPerRecipient) inbox.RemoveAt(0);

            return id;
        }
    }

    /// <summary>
    /// Everything after <paramref name="since"/> for one recipient. Entries are kept until they
    /// expire rather than deleted on read, so a response lost on the way back is not a lost signal.
    /// </summary>
    public IReadOnlyList<SignalDelivery> Since(string groupId, string to, long since)
    {
        var now = _clock();

        lock (_gate)
        {
            if (!_inboxes.TryGetValue(groupId + "|" + to, out var inbox)) return Array.Empty<SignalDelivery>();

            Prune(inbox, now);
            return inbox
                .Where(entry => entry.Id > since)
                .Select(entry => new SignalDelivery(entry.Id, entry.From, entry.Payload, entry.ReceivedAt))
                .ToArray();
        }
    }

    public int Count(string groupId, string to)
    {
        lock (_gate)
        {
            if (!_inboxes.TryGetValue(groupId + "|" + to, out var inbox)) return 0;

            Prune(inbox, _clock());
            return inbox.Count;
        }
    }

    public void ClearGroup(string groupId)
    {
        lock (_gate)
        {
            foreach (var key in _inboxes.Keys.Where(key => key.StartsWith(groupId + "|", StringComparison.Ordinal)).ToArray())
                _inboxes.Remove(key);
        }
    }

    private void Prune(List<Entry> inbox, DateTimeOffset now) =>
        inbox.RemoveAll(entry => now - entry.ReceivedAt > Ttl);

    private sealed record Entry(long Id, string From, string Payload, DateTimeOffset ReceivedAt);
}
