using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MyAiAssistant.Services.Permissions;

public sealed record OperationRecord(
    DateTimeOffset TimestampUtc,
    string ActionId,
    RiskCategory Category,
    string Summary,
    string? Target,
    string Decision,
    string Reason,
    string? Reviewer,
    long DurationMs,
    IReadOnlyList<string>? ParameterNames = null);

/// <summary>
/// Append-only local record of every authorization decision. It stores what was asked
/// for, what was decided and why — never credentials and never file contents.
/// </summary>
public sealed partial class OperationAudit
{
    private const int MaxFieldLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();
    private readonly string _path;

    public OperationAudit(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "audit");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "operations.jsonl");
    }

    public string LogPath => _path;

    public void Record(OperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = JsonSerializer.Serialize(record with
        {
            Summary = Scrub(record.Summary),
            Target = record.Target is null ? null : Scrub(record.Target),
            Reason = Scrub(record.Reason)
        }, JsonOptions);

        lock (_sync)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
        }
    }

    public IReadOnlyList<OperationRecord> Query(DateTimeOffset from, DateTimeOffset to)
    {
        if (!File.Exists(_path)) return [];
        var results = new List<OperationRecord>();
        foreach (var line in ReadLines())
        {
            try
            {
                var record = JsonSerializer.Deserialize<OperationRecord>(line, JsonOptions);
                if (record is not null && record.TimestampUtc >= from && record.TimestampUtc <= to)
                    results.Add(record);
            }
            catch (JsonException) { /* a damaged line must not hide the rest of the log */ }
        }
        return results;
    }

    public int Export(string destination, DateTimeOffset from, DateTimeOffset to)
    {
        var records = Query(from, to);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.WriteAllLines(destination, records.Select(r => JsonSerializer.Serialize(r, JsonOptions)));
        return records.Count;
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private string[] ReadLines()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException) { return []; }
        }
    }

    // Defence in depth: callers are expected to pass already-safe text, but a token that
    // slips into a summary must not become a permanent on-disk secret.
    internal static string Scrub(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var scrubbed = SecretPattern().Replace(value, "[已移除]");
        scrubbed = LongTokenPattern().Replace(scrubbed, "[已移除]");
        return scrubbed.Length <= MaxFieldLength ? scrubbed : scrubbed[..MaxFieldLength] + "…";
    }

    [GeneratedRegex(@"(?i)(sk-[a-z0-9_\-]{8,}|ghp_[a-z0-9]{8,}|gho_[a-z0-9]{8,}|xox[baprs]-[a-z0-9\-]{8,}|bearer\s+[a-z0-9._\-]{8,})")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_\-]{32,}={0,2}\b")]
    private static partial Regex LongTokenPattern();
}