using FairyAI_Android.Services.Storage;

namespace FairyAI_Android.Services.Notes;

/// <summary>
/// The notes file. One JSON file under the app's data directory, loaded once and kept in memory,
/// rewritten on every change.
///
/// Two rules are enforced here rather than in the UI. A note is only reported as saved when it
/// actually reached disk — a save that would not survive a restart is reported as a failure, not
/// kept in memory and shown as if it worked. And a draft carrying an id that does not exist is
/// refused instead of being turned into a new note, so "update" cannot silently duplicate.
/// </summary>
public sealed class NoteStore
{
    public const int MaxTitleLength = 120;
    public const int MaxBodyLength = 20000;
    public const int MaxTags = 10;
    public const int MaxTagLength = 24;

    private readonly object _gate = new();
    private readonly List<Note> _notes;
    private readonly Func<DateTimeOffset> _clock;

    public NoteStore(string filePath, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("notes need a file path", nameof(filePath));

        FilePath = filePath;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _notes = JsonListFile.Load<Note>(filePath, out var error);
        LoadError = error;
    }

    public string FilePath { get; }

    /// <summary>Set when the file on disk could not be read; the store then started empty.</summary>
    public string? LoadError { get; }

    public string? LastError { get; private set; }

    public int Count
    {
        get { lock (_gate) return _notes.Count(item => !item.IsDeleted); }
    }

    /// <summary>All live notes as copies, newest first, pinned first. Tombstones are not listed.</summary>
    public IReadOnlyList<Note> All()
    {
        lock (_gate)
        {
            var copy = _notes.Where(item => !item.IsDeleted).Select(Clone).ToList();
            copy.Sort((left, right) =>
            {
                var byPin = right.Pinned.CompareTo(left.Pinned);
                return byPin != 0 ? byPin : right.UpdatedAt.CompareTo(left.UpdatedAt);
            });
            return copy;
        }
    }

    public Note? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_gate)
        {
            var note = _notes.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
            return note is null ? null : Clone(note);
        }
    }

    public NoteSaveResult Save(NoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var title = (draft.Title ?? "").Trim();
        var body = (draft.Body ?? "").Trim();

        if (title.Length == 0 && body.Length == 0) return NoteSaveResult.Refused("笔记没有内容");
        if (title.Length == 0) title = FirstLine(body);

        if (title.Length > MaxTitleLength) return NoteSaveResult.Refused($"标题超过 {MaxTitleLength} 字");
        if (body.Length > MaxBodyLength) return NoteSaveResult.Refused($"内容超过 {MaxBodyLength} 字");

        var tags = NormalizeTags(draft.Tags);
        var now = _clock();

        lock (_gate)
        {
            Note note;

            if (string.IsNullOrWhiteSpace(draft.Id))
            {
                note = new Note
                {
                    Id = "n" + Guid.NewGuid().ToString("n")[..12],
                    CreatedAt = now,
                };
                _notes.Add(note);
            }
            else
            {
                var existing = _notes.FirstOrDefault(item => item.Id == draft.Id);
                if (existing is null) return NoteSaveResult.Refused("要修改的笔记不存在");

                note = existing;
                note.CreatedAt = note.CreatedAt == default ? now : note.CreatedAt;
            }

            var previousTitle = note.Title;
            var previousBody = note.Body;
            var previousTags = note.Tags;
            var previousPinned = note.Pinned;
            var previousUpdated = note.UpdatedAt;
            var previousDeletedAt = note.DeletedAt;

            note.Title = title;
            note.Body = body;
            note.Tags = tags;
            note.Pinned = draft.Pinned;
            note.UpdatedAt = now;
            note.DeletedAt = null; // editing a deleted note brings it back

            if (Persist())
            {
                LastError = null;
                return NoteSaveResult.Saved(Clone(note));
            }

            // Nothing reached disk, so nothing stays in memory either: a note that disappears on
            // restart is worse than a save that reports failure.
            if (string.IsNullOrWhiteSpace(draft.Id))
            {
                _notes.Remove(note);
            }
            else
            {
                note.Title = previousTitle;
                note.Body = previousBody;
                note.Tags = previousTags;
                note.Pinned = previousPinned;
                note.UpdatedAt = previousUpdated;
                note.DeletedAt = previousDeletedAt;
            }

            return NoteSaveResult.Refused(LastError ?? "保存失败");
        }
    }

    public bool Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_gate)
        {
            var note = _notes.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
            if (note is null) return false;

            var previousDeletedAt = note.DeletedAt;
            var previousUpdatedAt = note.UpdatedAt;

            // A tombstone, not a removal: the other device has to learn that this note is gone.
            note.DeletedAt = _clock();
            note.UpdatedAt = note.DeletedAt.Value;

            if (Persist())
            {
                LastError = null;
                return true;
            }

            note.DeletedAt = previousDeletedAt;
            note.UpdatedAt = previousUpdatedAt;
            return false;
        }
    }

    public bool SetPinned(string? id, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_gate)
        {
            var note = _notes.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
            if (note is null) return false;

            note.Pinned = pinned;
            note.UpdatedAt = _clock();
            if (Persist()) return true;

            note.Pinned = !pinned;
            return false;
        }
    }

    public IReadOnlyList<NoteHit> Search(string? query, int limit = 20)
    {
        List<Note> snapshot;
        lock (_gate) snapshot = _notes.Where(item => !item.IsDeleted).Select(Clone).ToList();

        return NoteSearch.Search(snapshot, query, limit);
    }

    /// <summary>
    /// Everything changed strictly after <paramref name="sinceMs"/>, tombstones included.
    ///
    /// Compared in milliseconds on both sides on purpose: the watermark has millisecond resolution, so
    /// comparing it against a full-precision timestamp made every note look "changed" because of the
    /// sub-millisecond remainder — the note then travelled on every single round.
    /// </summary>
    public IReadOnlyList<Note> ChangedSince(long sinceMs)
    {
        lock (_gate)
        {
            return _notes
                .Where(item => item.UpdatedAt.ToUnixTimeMilliseconds() > sinceMs)
                .Select(Clone)
                .ToList();
        }
    }

    /// <summary>Number of tombstones still on file (they are what makes deletions propagate).</summary>
    public int TombstoneCount
    {
        get { lock (_gate) return _notes.Count(item => item.IsDeleted); }
    }

    /// <summary>
    /// Drops tombstones older than <paramref name="olderThan"/>. They exist only so the other device
    /// learns about a deletion; after a few weeks keeping them just grows the file.
    /// </summary>
    public int PruneTombstones(DateTimeOffset olderThan)
    {
        lock (_gate)
        {
            var stale = _notes
                .Where(item => item.IsDeleted && item.DeletedAt is { } at && at < olderThan)
                .ToList();

            if (stale.Count == 0) return 0;

            foreach (var note in stale) _notes.Remove(note);

            if (Persist()) return stale.Count;

            _notes.AddRange(stale);
            return 0;
        }
    }

    /// <summary>
    /// Applies notes that came from the other device: last writer wins by <see cref="Note.UpdatedAt"/>,
    /// and an unknown id is added. Returns how many records changed; a write failure rolls the whole
    /// batch back and reports it, so a half-applied sync cannot be mistaken for a complete one.
    /// </summary>
    public (int Changed, string? Error) ApplyRemoteBatch(IEnumerable<Note> incoming)
    {
        if (incoming is null) return (0, null);

        lock (_gate)
        {
            var before = _notes.Select(Clone).ToList();
            var changed = 0;

            foreach (var remote in incoming)
            {
                if (remote is null || string.IsNullOrWhiteSpace(remote.Id)) continue;

                var local = _notes.FirstOrDefault(item => item.Id == remote.Id);

                if (local is null)
                {
                    _notes.Add(Clone(remote));
                    changed++;
                    continue;
                }

                if (remote.UpdatedAt <= local.UpdatedAt) continue;

                local.Title = remote.Title;
                local.Body = remote.Body;
                local.Tags = new List<string>(remote.Tags ?? new List<string>());
                local.Pinned = remote.Pinned;
                local.CreatedAt = remote.CreatedAt == default ? local.CreatedAt : remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.DeletedAt = remote.DeletedAt;
                changed++;
            }

            if (changed == 0) return (0, null);

            if (Persist())
            {
                LastError = null;
                return (changed, null);
            }

            _notes.Clear();
            _notes.AddRange(before);
            return (0, LastError ?? "同步结果没能保存");
        }
    }

    public static List<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var result = new List<string>();
        if (tags is null) return result;

        foreach (var raw in tags)
        {
            var tag = (raw ?? "").Trim();
            if (tag.Length == 0) continue;
            if (tag.Length > MaxTagLength) tag = tag[..MaxTagLength];
            if (result.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(tag);
            if (result.Count >= MaxTags) break;
        }

        return result;
    }

    private bool Persist()
    {
        if (JsonListFile.Save(FilePath, _notes, out var error)) return true;

        LastError = error;
        return false;
    }

    private static string FirstLine(string body)
    {
        var line = body.Split('\n')[0].Trim();
        if (line.Length > MaxTitleLength) line = line[..MaxTitleLength];
        return line.Length == 0 ? "无标题笔记" : line;
    }

    private static Note Clone(Note note) => new()
    {
        Id = note.Id,
        Title = note.Title,
        Body = note.Body,
        Tags = new List<string>(note.Tags ?? new List<string>()),
        Pinned = note.Pinned,
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt,
        DeletedAt = note.DeletedAt,
    };
}
