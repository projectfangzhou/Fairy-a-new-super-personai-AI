using System.Text.Json;

namespace MyAiAssistant.Services;

/// <summary>
/// The phone-facing side of notes.
///
/// One POST does both directions: the phone sends what it changed after its watermark, this machine
/// merges it, and the answer is everything this machine changed after the same watermark. Merging is
/// last-writer-wins on <c>UpdatedAt</c> — no clock is trusted for ordering beyond the stamps both sides
/// write themselves, and deletions travel as tombstones so a note removed on either side stays gone.
///
/// Authorization is the per-device token from pairing plus its own switch: notes are the user's own
/// writing, so they are not shared just because task summaries are.
/// </summary>
public sealed class PairedNoteBridge
{
    /// <summary>A round bigger than this is not a note sync, it is a bug or an attack.</summary>
    public const int MaxRequestBytes = 2 * 1024 * 1024;

    private const int MaxNotesPerRound = 500;
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(90);

    private readonly NoteService _notes;
    private readonly PairedDeviceTokens _tokens;
    private readonly Func<bool> _enabled;

    public PairedNoteBridge(NoteService notes, PairedDeviceTokens tokens, Func<bool> enabled)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
    }

    /// <summary>Null when the request may proceed; otherwise the reason code to return.</summary>
    public string? RejectReason(string? deviceName, string? token) =>
        SummarySyncAccess.RejectReason(_enabled(), _tokens.Verify(deviceName, token));

    /// <summary>Merges one round. The returned JSON is the response body, success or not.</summary>
    public (bool Ok, string Message, string Json) Handle(string? body)
    {
        var payload = body ?? "";
        if (payload.Length > MaxRequestBytes)
            return (false, "请求体太大", Json(new { success = false, error = "payload_too_large" }));

        long since;
        List<RemoteNote> incoming;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            since = root.TryGetProperty("since", out var sinceElement) && sinceElement.ValueKind == JsonValueKind.Number
                ? sinceElement.GetInt64()
                : 0;

            incoming = ReadNotes(root);
        }
        catch (JsonException)
        {
            return (false, "请求体不是合法 JSON", Json(new { success = false, error = "bad_json" }));
        }

        if (incoming.Count > MaxNotesPerRound)
            return (false, "这一轮笔记太多", Json(new { success = false, error = "too_many_notes" }));

        _notes.PruneTombstones(TombstoneLifetime);

        var applied = _notes.ApplyRemoteBatch(incoming);
        if (applied < 0)
            return (false, "笔记没能写入磁盘", Json(new { success = false, error = "notes_save_failed" }));

        var serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outgoing = _notes.ChangedSince(since);

        return (true, $"合并 {applied} 条，回传 {outgoing.Count} 条", Json(new
        {
            success = true,
            serverTime,
            notes = outgoing.Select(ToWire).ToArray(),
        }));
    }

    private static List<RemoteNote> ReadNotes(JsonElement root)
    {
        var notes = new List<RemoteNote>();
        if (!root.TryGetProperty("notes", out var array) || array.ValueKind != JsonValueKind.Array) return notes;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            notes.Add(new RemoteNote(
                id!,
                ReadString(item, "title"),
                ReadString(item, "body"),
                ReadTags(item),
                ReadLong(item, "createdAtMs"),
                ReadLong(item, "updatedAtMs"),
                item.TryGetProperty("deletedAtMs", out var deleted) && deleted.ValueKind == JsonValueKind.Number
                    ? deleted.GetInt64()
                    : null));
        }

        return notes;
    }

    private static object ToWire(NoteItem note) => new
    {
        id = note.Id,
        title = note.Title,
        body = note.Content,
        tags = note.Tags,
        pinned = false,
        createdAtMs = NoteService.ToUnixMs(note.CreatedAt),
        updatedAtMs = NoteService.ToUnixMs(note.UpdatedAt),
        deletedAtMs = note.DeletedAt is { } deleted ? NoteService.ToUnixMs(deleted) : (long?)null,
    };

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static long ReadLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt64(out var value)
            ? value
            : 0;

    private static string[] ReadTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var tag in tags.EnumerateArray())
        {
            var value = tag.GetString();
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
        }

        return list.ToArray();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
