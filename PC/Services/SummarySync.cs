using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.Notifications;

namespace MyAiAssistant.Services;

/// <summary>
/// What a paired phone is allowed to learn about this machine's work. Deliberately tiny: an id, a
/// scrubbed title, a state and a time. Prompts, outputs, error text and file paths are not part of
/// this shape, so they cannot leak by accident — the endpoint has nothing else to send.
/// </summary>
public sealed record TaskSummary(string TaskId, string Title, string State, DateTimeOffset UpdatedAt);

/// <summary>Builds the summary payload the phone polls for.</summary>
public static class SummarySyncPayload
{
    public const int DefaultMaxTasks = 20;

    public static string Build(IEnumerable<TaskSummary> summaries, int max = DefaultMaxTasks)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var tasks = summaries
            .GroupBy(summary => summary.TaskId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderByDescending(summary => summary.UpdatedAt)
            .Take(Math.Clamp(max, 1, 100))
            .Select(summary => new
            {
                taskId = summary.TaskId,
                // A task title is user-authored and can hold a path or a customer name; the phone
                // notification is exactly the wrong place for either.
                title = SystemNotificationPolicy.Scrub(summary.Title, 40),
                state = summary.State,
                updatedAt = summary.UpdatedAt
            })
            .ToArray();

        return JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, tasks });
    }
}

/// <summary>
/// The two conditions a summary request must meet, kept separate from the socket code so the rule
/// can be tested without standing up an HttpListener.
/// </summary>
public static class SummarySyncAccess
{
    public const string DisabledCode = "summary_sync_disabled";
    public const string UnpairedCode = "unpaired";

    /// <summary>Returns the rejection code, or null when the request may proceed.</summary>
    public static string? RejectReason(bool enabled, bool tokenVerified)
    {
        if (!enabled) return DisabledCode;
        if (!tokenVerified) return UnpairedCode;
        return null;
    }
}

/// <summary>
/// Per-device bearer tokens for the phone-facing endpoints.
///
/// This exists because the older LAN endpoints authenticate by a device *name* header, and a name is
/// not a secret: anyone who guesses the phone's name can call them. New endpoints do not repeat that
/// mistake — the token is issued once at pairing, only its hash is stored, and revoking a device is
/// deleting one line.
/// </summary>
public sealed class PairedDeviceTokens
{
    /// <summary>
    /// The process-wide store. The LAN service verifies against it and the control centre revokes
    /// through it, so a revoke is effective on the next request rather than after a restart.
    /// </summary>
    public static PairedDeviceTokens Shared { get; } = new();

    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, string> _hashes;

    /// <summary>Why the last load or save failed, when it did.</summary>
    public string? LastError { get; private set; }

    public PairedDeviceTokens(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FairyAI", "sync");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "paired-tokens.json");
        _hashes = Load();
    }

    public string StorePath => _path;

    public IReadOnlyList<string> Devices
    {
        get
        {
            lock (_sync) return _hashes.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Issues a fresh token for a device, replacing any earlier one. The raw value is returned once.</summary>
    public string Issue(string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_sync)
        {
            _hashes[deviceName] = Hash(token);
            SaveLocked();
        }
        return token;
    }

    public bool Verify(string? deviceName, string? token)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(token)) return false;

        string? expected;
        lock (_sync)
        {
            if (!_hashes.TryGetValue(deviceName, out expected)) return false;
        }

        var provided = Encoding.ASCII.GetBytes(Hash(token));
        var stored = Encoding.ASCII.GetBytes(expected);
        return provided.Length == stored.Length && CryptographicOperations.FixedTimeEquals(provided, stored);
    }

    public bool Revoke(string deviceName)
    {
        lock (_sync)
        {
            if (!_hashes.Remove(deviceName)) return false;
            SaveLocked();
            return true;
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable token file means "no device is trusted", which is the safe direction.
            LastError = $"令牌文件无法读取：{ex.GetType().Name}";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_hashes));
            File.Move(temporary, _path, overwrite: true);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the file re-requires pairing, which is recoverable; crashing is not.
            LastError = $"令牌文件无法保存：{ex.GetType().Name}";
        }
    }
}
