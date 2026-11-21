using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FairyAI_Android.Services;

public sealed class SignedUpdateService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly string _directory;
    private readonly string _currentVersion;
    private readonly string _trustedKey;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task> _notify;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _foreground;
    private string? _notifiedVersion;
    private bool _disposed;
    public string Status { get; private set; } = "disabled";

    public SignedUpdateService(string currentVersion, Func<string, CancellationToken, Task> notify,
        string? directory = null, HttpMessageHandler? handler = null, string? trustedKey = null)
    {
        _currentVersion = currentVersion;
        _trustedKey = trustedKey ?? PinnedUpdateKey.Base64;
        _notify = notify;
        _directory = directory ?? FileSystem.AppDataDirectory;
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
        }) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void StartForeground()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_foreground is not null) return;
            var source = new CancellationTokenSource();
            _foreground = source;
            _ = Task.Run(() => RunAsync(source));
        }
    }

    public void StopForeground()
    {
        lock (_lifecycleLock)
        {
            _foreground?.Cancel();
            _foreground = null;
        }
    }

    private async Task RunAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            do { await CheckOnceAsync(source.Token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(source.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_foreground, source)) _foreground = null;
                source.Dispose();
            }
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var token = timeout.Token;
            var configPath = Path.Combine(_directory, "updates.json");
            if (!File.Exists(configPath)) { Status = "disabled"; return; }
            using var config = SignedUpdateManifest.Parse(await ReadFileAsync(configPath, token).ConfigureAwait(false));
            var root = config.RootElement;
            SignedUpdateManifest.Exact(root, "manifest_url", "public_key");
            var url = SignedUpdateManifest.Text(root.GetProperty("manifest_url"));
            var publicKey = SignedUpdateManifest.Text(root.GetProperty("public_key"));
            if (url.Length == 0 || publicKey.Length == 0) { Status = "disabled"; return; }
            // The config file is user-writable; only the key compiled into this build is trusted.
            if (!string.Equals(publicKey.Trim(), _trustedKey, StringComparison.Ordinal)) { Status = "key-mismatch"; return; }
            var uri = SignedUpdateManifest.SafeUrl(url);
            SignedUpdateManifest.DecodeBase64(publicKey, SignedUpdateManifest.PublicKeyBytes);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength > SignedUpdateManifest.MaxBytes)
                throw new InvalidDataException("Invalid update response.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
            var manifest = SignedUpdateManifest.Verify(bytes, publicKey, DateTimeOffset.UtcNow);
            Remember(manifest, publicKey, token);
            token.ThrowIfCancellationRequested();
            if (manifest.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("Manifest expired during check.");
            if (manifest.AndroidVersion is null) { Status = "current"; return; }
            var newer = IsNewer(manifest.AndroidVersion, _currentVersion);
            Status = newer is null ? "uncomparable" : newer.Value ? "available" : "current";
            if (newer == true && _notifiedVersion != manifest.AndroidVersion)
            {
                // The network deadline must not expire while the user is reading the dialog.
                await _notify(manifest.AndroidVersion, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _notifiedVersion = manifest.AndroidVersion;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { Status = "error"; }
        finally { _checkLock.Release(); }
    }

    public static bool? IsNewer(string candidate, string current)
    {
        static Version? ParseVersion(string text)
        {
            if (!Regex.IsMatch(text, @"\A\d+(?:\.\d+){1,3}\z") || !Version.TryParse(text, out var version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
        var left = ParseVersion(candidate);
        var right = ParseVersion(current);
        return left is null || right is null ? null : left > right;
    }

    private void Remember(VerifiedUpdateManifest manifest, string publicKey, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        using var stateLock = new FileStream(Path.Combine(_directory, "updates-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(_directory, "updates-state.json");
        var fingerprint = Convert.ToHexString(SHA256.HashData(SignedUpdateManifest.DecodeBase64(publicKey, SignedUpdateManifest.PublicKeyBytes))).ToLowerInvariant();
        if (File.Exists(path))
        {
            using var old = SignedUpdateManifest.Parse(ReadFileAsync(path, token).GetAwaiter().GetResult());
            var root = old.RootElement;
            SignedUpdateManifest.Exact(root, "key", "sequence", "digest");
            var key = SignedUpdateManifest.Text(root.GetProperty("key"));
            var digest = SignedUpdateManifest.Text(root.GetProperty("digest"));
            var sequence = SignedUpdateManifest.Sequence(root.GetProperty("sequence"));
            if (!Regex.IsMatch(key, @"\A[a-f0-9]{64}\z") || !Regex.IsMatch(digest, @"\A[a-f0-9]{64}\z") || key != fingerprint ||
                manifest.Sequence < sequence || (manifest.Sequence == sequence && manifest.Digest != digest))
                throw new InvalidDataException("Rollback, key change or damaged update state.");
            if (manifest.Sequence == sequence) return;
        }
        var temporary = Path.Combine(_directory, ".updates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { key = fingerprint, sequence = manifest.Sequence, digest = manifest.Digest });
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return await ReadBoundedAsync(file, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > SignedUpdateManifest.MaxBytes) throw new InvalidDataException("Update data is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            StopForeground();
            _http.Dispose();
        }
    }
}
