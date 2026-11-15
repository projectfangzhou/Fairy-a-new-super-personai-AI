using System.Text.Json;

namespace FairyAI_Android.Services.Storage;

/// <summary>
/// Persists a list of records to one JSON file.
///
/// Two behaviours matter more than the serialisation itself. Writes go through a temporary file and
/// then replace, so a process killed mid-write cannot leave a half file behind. And a file that no
/// longer parses is moved aside instead of being deleted or silently treated as empty: the user's
/// notes are theirs, so a damaged file is kept for recovery and the reason is reported.
/// </summary>
public static class JsonListFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static List<T> Load<T>(string path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path)) { error = "没有给出存储路径"; return new List<T>(); }
        if (!File.Exists(path)) return new List<T>();

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();

            var items = JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>();
            items.RemoveAll(item => item is null);
            return items;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"读取 {Path.GetFileName(path)} 失败：{ex.GetType().Name}";
            Quarantine(path);
            return new List<T>();
        }
    }

    public static bool Save<T>(string path, IEnumerable<T> items, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path)) { error = "没有给出存储路径"; return false; }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items.ToList(), Options);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"写入 {Path.GetFileName(path)} 失败：{ex.GetType().Name}";
            return false;
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            var damaged = $"{path}.damaged-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, damaged, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keeping the app usable matters more than preserving the copy; the read error is
            // reported either way, so nothing is hidden.
        }
    }
}
