namespace FairyAI_Android.Services.Notes;

/// <summary>One note. Plain data: the store owns identity and timestamps, never the caller.</summary>
public sealed class Note
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public List<string> Tags { get; set; } = new();

    public bool Pinned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Set when the note was deleted. The record stays in the file as a tombstone so a synced copy on
    /// another device can be deleted too — a note removed on the phone must not come back on the next
    /// sync. Live notes and tombstones are told apart everywhere the list is read.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>
/// What a caller may set. There is no id on a new note and the id of an existing one is matched
/// exactly, so "save" cannot quietly overwrite a different note.
/// </summary>
public sealed record NoteDraft(string Title, string Body, IReadOnlyList<string>? Tags = null)
{
    public bool Pinned { get; init; }

    public string? Id { get; init; }
}

/// <summary>The result of a save attempt, including why it was refused.</summary>
public sealed record NoteSaveResult(bool Ok, string Message, Note? Note)
{
    public static NoteSaveResult Refused(string reason) => new(false, reason, null);

    public static NoteSaveResult Saved(Note note) => new(true, $"已保存《{note.Title}》", note);
}

/// <summary>One search result: the note, its score, and where the match was.</summary>
public sealed record NoteHit(Note Note, int Score, string Snippet);
