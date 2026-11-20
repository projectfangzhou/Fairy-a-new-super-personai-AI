using System.Security.Cryptography;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>What a local embedding model is and what it costs the user to keep it.</summary>
public sealed record LocalEmbeddingModel(
    string Id,
    string DisplayName,
    string FileName,
    string VocabFileName,
    long ApproximateBytes,
    string License)
{
    public string ApproximateSize => ApproximateBytes >= 1024L * 1024 * 1024
        ? $"{ApproximateBytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
        : $"{ApproximateBytes / 1024.0 / 1024.0:0} MB";

    /// <summary>
    /// The model that ships inside the APK. It is a BERT-family Chinese embedding model quantised to
    /// int8: 23 MB instead of 600 MB, and it runs in well under a second per passage on a phone.
    ///
    /// A 0.6B model would need roughly 600 MB on disk, a different tokenizer (BPE, not WordPiece) and
    /// several hundred MB of RAM; that is a download-on-demand model, not a bundled one. The store and
    /// runtime here are not tied to this particular file, so pointing them at a bigger model later is
    /// a matter of adding another entry.
    /// </summary>
    public static LocalEmbeddingModel BundledChinese { get; } = new(
        "bge-small-zh-v1.5-int8",
        "bge-small-zh-v1.5（int8，随包）",
        "bge-small-zh-v1.5-int8.onnx",
        "vocab.txt",
        23L * 1024 * 1024,
        "MIT");
}

public enum LocalModelState
{
    /// <summary>No file on this phone.</summary>
    Missing,

    /// <summary>A partial file from an interrupted transfer; it must not be used.</summary>
    Partial,

    /// <summary>A complete model and its vocabulary are in place.</summary>
    Ready,
}

/// <summary>
/// Owns the local embedding model files: where they live, how they get there, and whether they can be
/// trusted.
///
/// Three ways in, because the file is too big for an app store download in the 0.6B case and may not
/// be reachable at all: the copy inside the APK, a file the user already has, or a URL they supply.
/// All three end the same way — size check, then an atomic move, so an interrupted transfer leaves a
/// <c>.part</c> that is never mistaken for a model.
/// </summary>
public sealed class LocalEmbeddingStore : IDisposable
{
    private const long MinimumPlausibleBytes = 4L * 1024 * 1024;

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LocalEmbeddingStore(string directory, HttpClient? http = null)
    {
        _directory = directory;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _ownsHttp = http is null;
    }

    public string Directory => _directory;

    public string? LastError { get; private set; }

    public string PathOf(LocalEmbeddingModel model) => Path.Combine(_directory, model.FileName);

    public string VocabPathOf(LocalEmbeddingModel model) => Path.Combine(_directory, model.VocabFileName);

    private string PartialPathOf(LocalEmbeddingModel model) => PathOf(model) + ".part";

    public LocalModelState StateOf(LocalEmbeddingModel model)
    {
        try
        {
            if (File.Exists(PathOf(model)) && File.Exists(VocabPathOf(model))) return LocalModelState.Ready;
            return File.Exists(PartialPathOf(model)) ? LocalModelState.Partial : LocalModelState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"读不到模型目录：{ex.GetType().Name}";
            return LocalModelState.Missing;
        }
    }

    public long SizeOnDisk(LocalEmbeddingModel model)
    {
        try
        {
            var path = PathOf(model);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Copies the model shipped inside the APK into app storage, once. Reading it straight from the
    /// package is not possible for the native runtime, which needs a real file path.
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallBundledAsync(
        LocalEmbeddingModel model,
        Func<string, Task<Stream?>> openAsset,
        CancellationToken cancellationToken = default)
    {
        if (StateOf(model) == LocalModelState.Ready) return (true, "已经在手机上");

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            foreach (var (assetName, target) in new[]
                     {
                         ("embedding/" + model.FileName, PathOf(model)),
                         ("embedding/" + model.VocabFileName, VocabPathOf(model)),
                     })
            {
                await using var source = await openAsset(assetName).ConfigureAwait(false);
                if (source is null) return (false, $"安装包里找不到 {assetName}");

                var partial = target + ".part";
                await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

                File.Move(partial, target, overwrite: true);
            }

            LastError = null;
            return (true, $"已把随包模型放进手机存储（{SizeOnDisk(model) / 1024.0 / 1024.0:0} MB）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"安装随包模型失败：{ex.GetType().Name}";
            return (false, LastError);
        }
    }

    /// <summary>Downloads to a .part file and only then moves it into place.</summary>
    public async Task<(bool Ok, string Message)> DownloadAsync(
        LocalEmbeddingModel model,
        string url,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "没有给出模型的下载地址");

        var partial = PartialPathOf(model);

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"下载失败：HTTP {(int)response.StatusCode}";
                return (false, LastError);
            }

            var expected = response.Content.Headers.ContentLength ?? 0;
            long received = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (expected > 0) progress?.Report((double)received / expected);
                }
            }

            if (received < MinimumPlausibleBytes)
            {
                File.Delete(partial);
                LastError = $"下载到的文件只有 {received / 1024} KB，不像一个模型文件，已删除";
                return (false, LastError);
            }

            if (expected > 0 && received != expected)
            {
                File.Delete(partial);
                LastError = $"下载不完整（{received}/{expected} 字节），已删除";
                return (false, LastError);
            }

            File.Move(partial, PathOf(model), overwrite: true);
            LastError = null;
            return (true, $"已下载 {model.DisplayName}（{received / 1024.0 / 1024.0:0} MB）");
        }
        catch (OperationCanceledException)
        {
            return (false, "下载被取消了，未完成的文件已保留为 .part");
        }
        catch (HttpRequestException ex)
        {
            LastError = $"连不上下载地址：{ex.Message}";
            return (false, LastError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"写入模型文件失败：{ex.GetType().Name}";
            return (false, LastError);
        }
    }

    /// <summary>Copies a model the user already has on the phone (or pushed over adb).</summary>
    public (bool Ok, string Message) Import(LocalEmbeddingModel model, string sourcePath, string? vocabSourcePath = null)
    {
        try
        {
            if (!File.Exists(sourcePath)) return (false, $"找不到文件：{sourcePath}");

            var size = new FileInfo(sourcePath).Length;
            if (size < MinimumPlausibleBytes)
                return (false, $"这个文件只有 {size / 1024} KB，不像一个模型文件");

            System.IO.Directory.CreateDirectory(_directory);

            var partial = PartialPathOf(model);
            File.Copy(sourcePath, partial, overwrite: true);
            File.Move(partial, PathOf(model), overwrite: true);

            if (!string.IsNullOrWhiteSpace(vocabSourcePath))
            {
                if (!File.Exists(vocabSourcePath))
                    return (false, $"模型已放入，但词表文件不存在：{vocabSourcePath}");

                File.Copy(vocabSourcePath, VocabPathOf(model), overwrite: true);
            }

            LastError = null;
            return (true, $"已导入 {model.DisplayName}（{size / 1024.0 / 1024.0:0} MB）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"导入失败：{ex.GetType().Name}";
            return (false, LastError);
        }
    }

    public bool Delete(LocalEmbeddingModel model)
    {
        try
        {
            foreach (var path in new[] { PathOf(model), PartialPathOf(model), VocabPathOf(model) })
                if (File.Exists(path)) File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"删除失败：{ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>SHA-256 of the model file, for a record of what is actually installed.</summary>
    public string? Fingerprint(LocalEmbeddingModel model)
    {
        try
        {
            var path = PathOf(model);
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
