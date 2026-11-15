using System.Text.Json;

namespace FairyAI_Android.Services.Notes;

/// <summary>
/// The two watermarks a sync round needs, and why there are two.
///
/// <see cref="LastSyncAt"/> is in the <em>computer's</em> time domain: the computer decides what to
/// return by comparing it to its own timestamps, so only a value it produced is meaningful. It is
/// what travels as <c>since</c>.
///
/// <see cref="LastPushedAt"/> is in <em>this phone's</em> time domain: it decides which local notes
/// still have to be sent. Using the computer's value for that was a real bug — a phone whose clock
/// runs ahead of the computer re-sent every note on every round, and a watermark in the future would
/// hold back notes written before it.
///
/// Tombstones are not stored here: they live in the notes file, so deleting a note is one write
/// instead of two that can disagree.
/// </summary>
public sealed class NoteSyncState
{
    private readonly object _gate = new();

    public NoteSyncState(string filePath)
    {
        FilePath = filePath;

        try
        {
            if (!File.Exists(filePath)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = document.RootElement;

            if (root.TryGetProperty("lastSyncAt", out var syncAt) && syncAt.ValueKind == JsonValueKind.Number)
                LastSyncAt = syncAt.GetInt64();

            if (root.TryGetProperty("lastPushedAt", out var pushedAt) && pushedAt.ValueKind == JsonValueKind.Number)
                LastPushedAt = pushedAt.GetInt64();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastSyncAt = 0;
            LastPushedAt = 0;
        }
    }

    public string FilePath { get; }

    /// <summary>Computer-domain watermark: what was last received from it.</summary>
    public long LastSyncAt { get; private set; }

    /// <summary>Phone-domain watermark: the newest local change already sent.</summary>
    public long LastPushedAt { get; private set; }

    /// <summary>Records a completed round. Both values only move forward.</summary>
    public void Record(long serverTimeMs, long pushedUpToMs)
    {
        lock (_gate)
        {
            if (serverTimeMs > LastSyncAt) LastSyncAt = serverTimeMs;
            if (pushedUpToMs > LastPushedAt) LastPushedAt = pushedUpToMs;

            Save();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            LastSyncAt = 0;
            LastPushedAt = 0;
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                lastSyncAt = LastSyncAt,
                lastPushedAt = LastPushedAt,
            }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The watermark is an optimisation: losing it re-sends a round, which the merge handles
            // by timestamps anyway.
        }
    }
}
