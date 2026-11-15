using System.Text.Json;

namespace FairyAI_Android.Services.Notes;

/// <summary>One note on the wire. Times are unix milliseconds in UTC — no time zones in the payload.</summary>
public sealed record SyncNote(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<string> Tags,
    bool Pinned,
    long CreatedAtMs,
    long UpdatedAtMs,
    long? DeletedAtMs);

/// <summary>The server's answer: everything it changed after the watermark this client sent.</summary>
public sealed record NoteSyncResponse(long ServerTime, IReadOnlyList<SyncNote> Notes);

/// <summary>What a sync round produced, for the UI and for the log.</summary>
public sealed record NoteSyncOutcome(bool Ok, string Message, int Pushed, int Pulled, long ServerTime)
{
    public static NoteSyncOutcome Fail(string message) => new(false, message, 0, 0, 0);
}

/// <summary>The three things a round needs from configuration, passed in as plain values.</summary>
public sealed record NoteSyncSettings(bool Enabled, string Endpoint, string DeviceName, string Token)
{
    public static NoteSyncSettings Off { get; } = new(false, "", "", "");
}

/// <summary>
/// The JSON on the sync wire.
///
/// Deletions travel as notes with a <c>deletedAtMs</c> stamp rather than as a second list: one shape
/// to serialize, one shape to merge, and a tombstone cannot be forgotten by a code path that only
/// looks at notes.
/// </summary>
public static class NoteSyncWire
{
    /// <summary>
    /// camelCase on the wire. Both ends read <c>id</c>/<c>title</c>/<c>body</c>, so the serializer has
    /// to be told: records default to PascalCase and the other side would drop every note silently.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SyncNote ToWire(Note note) => new(
        note.Id,
        note.Title,
        note.Body,
        new List<string>(note.Tags ?? new List<string>()),
        note.Pinned,
        note.CreatedAt.ToUnixTimeMilliseconds(),
        note.UpdatedAt.ToUnixTimeMilliseconds(),
        note.DeletedAt?.ToUnixTimeMilliseconds());

    public static Note FromWire(SyncNote wire) => new()
    {
        Id = wire.Id,
        Title = wire.Title ?? "",
        Body = wire.Body ?? "",
        Tags = new List<string>(wire.Tags ?? Array.Empty<string>()),
        Pinned = wire.Pinned,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(wire.CreatedAtMs),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(wire.UpdatedAtMs),
        DeletedAt = wire.DeletedAtMs is { } deleted ? DateTimeOffset.FromUnixTimeMilliseconds(deleted) : null,
    };

    /// <summary>The body this client sends: the watermark plus everything it changed since then.</summary>
    public static string BuildRequestBody(long since, IEnumerable<Note> changed, string deviceName = "")
    {
        var notes = (changed ?? Enumerable.Empty<Note>())
            .Where(note => note is not null)
            .Select(ToWire)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            since,
            device = deviceName ?? "",
            notes,
        }, WireOptions);
    }

    public static (bool Ok, string? Error, NoteSyncResponse? Response) ParseResponse(int statusCode, string? body)
    {
        var payload = body ?? "";

        if (statusCode < 200 || statusCode > 299)
            return (false, $"电脑返回 HTTP {statusCode}{Detail(payload)}", null);

        if (string.IsNullOrWhiteSpace(payload))
            return (false, "电脑返回了空响应", null);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (false, "电脑返回的 JSON 不是对象", null);

            var serverTime = root.TryGetProperty("serverTime", out var time) && time.ValueKind == JsonValueKind.Number
                ? time.GetInt64()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var notes = new List<SyncNote>();

            if (root.TryGetProperty("notes", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    notes.Add(new SyncNote(
                        id!,
                        item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        item.TryGetProperty("body", out var noteBody) ? noteBody.GetString() ?? "" : "",
                        ReadTags(item),
                        item.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True,
                        ReadLong(item, "createdAtMs"),
                        ReadLong(item, "updatedAtMs"),
                        item.TryGetProperty("deletedAtMs", out var deleted) && deleted.ValueKind == JsonValueKind.Number
                            ? deleted.GetInt64()
                            : null));
                }
            }

            return (true, null, new NoteSyncResponse(serverTime, notes));
        }
        catch (JsonException)
        {
            return (false, "电脑返回的内容不是 JSON", null);
        }
    }

    private static IReadOnlyList<string> ReadTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var tag in tags.EnumerateArray())
        {
            var value = tag.GetString();
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
        }

        return list;
    }

    private static long ReadLong(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
            return 0;

        return element.TryGetInt64(out var value) ? value : 0;
    }

    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length > 160) flat = flat[..160] + "…";

        return "（" + flat + "）";
    }
}
