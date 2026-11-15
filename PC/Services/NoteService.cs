using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAiAssistant.Services;

public class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Set when the note is deleted. The record stays in the file as a tombstone so a paired phone
    /// deletes its copy too; a note deleted here must not come back on the next sync.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>One note as it arrives from a paired phone. Times are unix milliseconds (UTC).</summary>
public sealed record RemoteNote(
    string Id,
    string Title,
    string Content,
    string[] Tags,
    long CreatedAtMs,
    long UpdatedAtMs,
    long? DeletedAtMs);

/// <summary>
/// Simple note-taking service with search. Stores notes in JSON file.
/// AI can create, search, list, and delete notes. Deletions leave tombstones so sync can propagate
/// them, and every read path filters those out — a tombstone is bookkeeping, not a note.
/// </summary>
public class NoteService
{
    private readonly List<NoteItem> _notes = new();
    private readonly string _filePath;
    private readonly string _logPath;

    public int Count => _notes.Count(n => !n.IsDeleted);

    /// <summary>Tombstones still on file (they are what makes a deletion reach the phone).</summary>
    public int TombstoneCount => _notes.Count(n => n.IsDeleted);

    public NoteService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notes.json");
        _logPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "fairy.log");
        Load();
    }

    public NoteItem Create(string title, string content, string[] tags)
    {
        var note = new NoteItem
        {
            Title = title,
            Content = content,
            Tags = tags,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _notes.Add(note);
        Save();
        Log($"Note created: {note.Title} [{note.Id}]");
        return note;
    }

    public NoteItem? Get(string id) =>
        _notes.FirstOrDefault(n => n.Id == id && !n.IsDeleted);

    public List<NoteItem> List(int limit = 20) =>
        _notes.Where(n => !n.IsDeleted).OrderByDescending(n => n.UpdatedAt).Take(limit).ToList();

    public List<NoteItem> Search(string query)
    {
        var q = query.ToLowerInvariant();
        return _notes
            .Where(n => !n.IsDeleted)
            .Where(n => n.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        n.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        n.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(n => n.UpdatedAt)
            .ToList();
    }

    public bool Delete(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id && !n.IsDeleted);
        if (note == null) return false;

        note.DeletedAt = DateTime.Now;
        note.UpdatedAt = note.DeletedAt.Value;
        Save();
        Log($"Note deleted: {note.Title} [{note.Id}]");
        return true;
    }

    public NoteItem? Update(string id, string? title, string? content, string[]? tags)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id && !n.IsDeleted);
        if (note == null) return null;

        if (title != null) note.Title = title;
        if (content != null) note.Content = content;
        if (tags != null) note.Tags = tags;
        note.UpdatedAt = DateTime.Now;
        note.DeletedAt = null;
        Save();
        Log($"Note updated: {note.Title} [{note.Id}]");
        return note;
    }

    /// <summary>Everything changed strictly after the given unix-ms watermark, tombstones included.</summary>
    public List<NoteItem> ChangedSince(long sinceMs)
    {
        return _notes
            .Where(n => ToUnixMs(n.UpdatedAt) > sinceMs)
            .ToList();
    }

    public static long ToUnixMs(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Local : value.Kind))
            .ToUnixTimeMilliseconds();

    /// <summary>
    /// Applies what the phone sent: last writer wins by <c>UpdatedAt</c>, unknown ids are added.
    /// Returns how many records changed, or -1 when the write failed (the batch is rolled back then,
    /// so a failed sync cannot look like a successful one).
    /// </summary>
    public int ApplyRemoteBatch(IEnumerable<RemoteNote> incoming)
    {
        if (incoming is null) return 0;

        var before = _notes.Select(Clone).ToList();
        var changed = 0;

        foreach (var remote in incoming)
        {
            if (remote is null || string.IsNullOrWhiteSpace(remote.Id)) continue;

            var local = _notes.FirstOrDefault(n => n.Id == remote.Id);
            var remoteUpdated = DateTimeOffset.FromUnixTimeMilliseconds(remote.UpdatedAtMs).LocalDateTime;

            if (local is null)
            {
                _notes.Add(new NoteItem
                {
                    Id = remote.Id,
                    Title = remote.Title ?? "",
                    Content = remote.Content ?? "",
                    Tags = remote.Tags ?? Array.Empty<string>(),
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(remote.CreatedAtMs).LocalDateTime,
                    UpdatedAt = remoteUpdated,
                    DeletedAt = remote.DeletedAtMs is { } deleted
                        ? DateTimeOffset.FromUnixTimeMilliseconds(deleted).LocalDateTime
                        : null,
                });
                changed++;
                continue;
            }

            if (remoteUpdated <= local.UpdatedAt) continue;

            local.Title = remote.Title ?? "";
            local.Content = remote.Content ?? "";
            local.Tags = remote.Tags ?? Array.Empty<string>();
            local.UpdatedAt = remoteUpdated;
            local.DeletedAt = remote.DeletedAtMs is { } stamp
                ? DateTimeOffset.FromUnixTimeMilliseconds(stamp).LocalDateTime
                : null;
            changed++;
        }

        if (changed == 0) return 0;

        if (Save()) return changed;

        _notes.Clear();
        _notes.AddRange(before);
        return -1;
    }

    /// <summary>Drops tombstones older than the given age; they exist only to carry a deletion across.</summary>
    public int PruneTombstones(TimeSpan olderThan)
    {
        var cutoff = DateTime.Now - olderThan;
        var stale = _notes.Where(n => n.IsDeleted && n.DeletedAt < cutoff).ToList();
        if (stale.Count == 0) return 0;

        foreach (var note in stale) _notes.Remove(note);
        if (Save()) return stale.Count;

        _notes.AddRange(stale);
        return 0;
    }

    private static NoteItem Clone(NoteItem note) => new()
    {
        Id = note.Id,
        Title = note.Title,
        Content = note.Content,
        Tags = note.Tags,
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt,
        DeletedAt = note.DeletedAt,
    };

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var saved = JsonSerializer.Deserialize<List<NoteItem>>(json);
                if (saved != null) _notes.AddRange(saved);
            }
        }
        catch { }
    }

    private bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true });
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] [Notes] {msg}\n"); } catch { }
    }
}

// ===== Tool implementations =====

public class CreateNoteTool : ITool
{
    private readonly NoteService _notes;
    public string Name => "create_note";
    public string Description => "Create a new note in the knowledge base. Use this when the user wants to save information for later reference.";
    public List<ToolParameter> Parameters => new()
    {
        new ToolParameter { Name = "title", Description = "Note title" },
        new ToolParameter { Name = "content", Description = "Note content" },
        new ToolParameter { Name = "tags", Description = "Comma-separated tags", Required = false }
    };

    public CreateNoteTool(NoteService notes) => _notes = notes;

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var title = args["title"]?.GetValue<string>() ?? "Untitled";
        var content = args["content"]?.GetValue<string>() ?? "";
        var tagsStr = args["tags"]?.GetValue<string>() ?? "";
        var tags = tagsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var note = _notes.Create(title, content, tags);
        return Task.FromResult($"Note created: [{note.Id}] {note.Title}");
    }
}

public class SearchNotesTool : ITool
{
    private readonly NoteService _notes;
    public string Name => "search_notes";
    public string Description => "Search notes by keyword in title, content, or tags";
    public List<ToolParameter> Parameters => new()
    {
        new ToolParameter { Name = "query", Description = "Search keyword" }
    };

    public SearchNotesTool(NoteService notes) => _notes = notes;

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>() ?? "";
        var results = _notes.Search(query);

        if (results.Count == 0) return Task.FromResult("No notes found.");

        var sb = new StringBuilder();
        foreach (var n in results.Take(10))
        {
            sb.AppendLine($"[{n.Id}] {n.Title}");
            sb.AppendLine($"  Tags: {string.Join(", ", n.Tags)}");
            sb.AppendLine($"  {n.Content[..Math.Min(200, n.Content.Length)]}");
            sb.AppendLine();
        }
        return Task.FromResult(sb.ToString());
    }
}

public class ListNotesTool : ITool
{
    private readonly NoteService _notes;
    public string Name => "list_notes";
    public string Description => "List recent notes (most recent first)";
    public List<ToolParameter> Parameters => new()
    {
        new ToolParameter { Name = "limit", Description = "Max notes to return (default 10)", Type = "number", Required = false }
    };

    public ListNotesTool(NoteService notes) => _notes = notes;

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var limit = (int)(args["limit"]?.GetValue<double>() ?? 10);
        var notes = _notes.List(limit);

        if (notes.Count == 0) return Task.FromResult("No notes.");

        var sb = new StringBuilder();
        foreach (var n in notes)
            sb.AppendLine($"[{n.Id}] {n.Title} · {n.UpdatedAt:MM-dd HH:mm}");
        return Task.FromResult(sb.ToString());
    }
}

public class DeleteNoteTool : ITool
{
    private readonly NoteService _notes;
    public string Name => "delete_note";
    public string Description => "Delete a note by ID";
    public List<ToolParameter> Parameters => new()
    {
        new ToolParameter { Name = "id", Description = "Note ID" }
    };

    public DeleteNoteTool(NoteService notes) => _notes = notes;

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var id = args["id"]?.GetValue<string>() ?? "";
        var ok = _notes.Delete(id);
        return Task.FromResult(ok ? $"Note {id} deleted." : $"Note {id} not found.");
    }
}
