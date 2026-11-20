using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Passage vectors, cached on disk.
///
/// Embedding costs money and latency, and the same paragraph is embedded again on every process start
/// unless it is kept. The key includes the model name: switching embedding models must not silently
/// compare vectors from two different spaces.
/// </summary>
public sealed class EmbeddingCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly string _path;

    public EmbeddingCache(string path)
    {
        _path = path;
        Load();
    }

    public string FilePath => _path;

    public int Count
    {
        get { lock (_gate) return _vectors.Count; }
    }

    public string? LastError { get; private set; }

    /// <summary>Stable key for one passage under one model.</summary>
    public static string Key(string model, string document, string passage)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(document + "\u0000" + (passage ?? "").Trim()));
        return model + ":" + Convert.ToHexString(hash)[..24];
    }

    public float[]? Get(string key)
    {
        lock (_gate) return _vectors.TryGetValue(key, out var vector) ? vector : null;
    }

    public void Put(string key, float[] vector)
    {
        lock (_gate) _vectors[key] = vector;
    }

    /// <summary>Vectors of every other model are dropped: they cannot be compared to the current one.</summary>
    public int PruneToModel(string model)
    {
        lock (_gate)
        {
            var stale = _vectors.Keys
                .Where(key => !key.StartsWith(model + ":", StringComparison.Ordinal))
                .ToList();

            foreach (var key in stale) _vectors.Remove(key);
            return stale.Count;
        }
    }

    public bool Save()
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_vectors));
                File.Move(temporary, _path, overwrite: true);
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = $"保存向量缓存失败：{ex.GetType().Name}";
                return false;
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;

                var vector = new List<float>();
                foreach (var value in property.Value.EnumerateArray())
                    vector.Add((float)value.GetDouble());

                if (vector.Count > 0) _vectors[property.Name] = vector.ToArray();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged cache is not worth recovering: the vectors can be recomputed.
            LastError = $"向量缓存读不出来（{ex.GetType().Name}），会重新计算";
            _vectors.Clear();
        }
    }
}
