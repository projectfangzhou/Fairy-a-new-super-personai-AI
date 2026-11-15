using System.IO;
using System.Text.Json;

namespace MyAiAssistant.Services.Transfer;

public sealed record TransferFileProgress(
    TransferFile File,
    int ChunkCount,
    int ReceivedChunks,
    bool Completed,
    string? SavedPath)
{
    public int MissingChunks => Math.Max(0, ChunkCount - ReceivedChunks);
}

public sealed record TransferAcceptResult(bool Ok, string Message, TransferFrameKind Kind = TransferFrameKind.Chunk)
{
    public static TransferAcceptResult Refuse(string message, TransferFrameKind kind = TransferFrameKind.Chunk) =>
        new(false, message, kind);
}

/// <summary>
/// The receiving half of a transfer.
///
/// It writes into a session directory that only this class names, never into a path the sender
/// chose, and it only moves a file into place after its whole hash matches. A frame that fails
/// authentication is dropped without writing anything — a refused transfer leaves no partial file
/// behind under the real name.
///
/// Progress survives the process: the chunk map is written into the session directory as it goes,
/// so a transfer that was cut off can be picked up by the same session id later instead of starting
/// over. That is the whole point of keeping the partial files rather than deleting them on failure.
/// </summary>
public sealed class TransferReceiver : IDisposable
{
    public const int MaxFiles = 64;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a finished or abandoned session record is kept before it is swept away.</summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromHours(24);

    /// <summary>How long a finished or abandoned session record is kept before it is swept away.</summary>n    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly byte[] _groupKey;
    private readonly string _destinationRoot;
    private readonly int _chunkSize;
    private readonly List<IncomingFile> _files = [];
    private string _sessionDirectory = string.Empty;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    public TransferReceiver(byte[] groupKey, string destinationRoot, int chunkSize = TransferFrames.MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));
        if (string.IsNullOrWhiteSpace(destinationRoot)) throw new ArgumentException("接收目录不能为空", nameof(destinationRoot));
        if (chunkSize is < 1024 or > TransferFrames.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "分块大小需在 1KB 到 64KB 之间");

        // Own a copy: the caller may wipe its array once the receiver exists.
        _groupKey = (byte[])groupKey.Clone();
        _destinationRoot = Path.GetFullPath(destinationRoot);
        _chunkSize = chunkSize;
    }

    public TransferManifest? Manifest { get; private set; }

    /// <summary>The chunk size this receiver was built with; a sender has to match it.</summary>
    public int ChunkSize => _chunkSize;

    /// <summary>Set when a manifest continued a transfer that had already started.</summary>
    public bool Resumed { get; private set; }

    public IReadOnlyList<TransferFileProgress> Progress => _files
        .Select(file => new TransferFileProgress(file.File, file.ChunkCount, file.ReceivedCount, file.Completed, file.SavedPath))
        .ToArray();

    /// <summary>Chunk indexes still missing for a file, so a resume can ask for exactly those.</summary>
    public IReadOnlyList<int> MissingChunks(int fileIndex) =>
        fileIndex >= 0 && fileIndex < _files.Count
            ? Enumerable.Range(0, _files[fileIndex].Received.Length).Where(i => !_files[fileIndex].Received[i]).ToArray()
            : Array.Empty<int>();

    /// <summary>What the sender should send next. Empty lists mean that file is already whole.</summary>
    public TransferResumePlan? ResumePlan()
    {
        if (Manifest is null) return null;

        var entries = _files
            .Select((file, index) => new TransferFileResume(index, MissingChunks(index)))
            .ToArray();

        return new TransferResumePlan(Manifest.SessionId, entries);
    }

    public TransferAcceptResult Accept(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!TransferFrames.TryDecode(frame, _groupKey, out var decoded, out var error) || decoded is null)
            return TransferAcceptResult.Refuse(error);

        switch (decoded.Kind)
        {
            case TransferFrameKind.Manifest:
                return AcceptManifest(decoded);

            case TransferFrameKind.Chunk:
                return AcceptChunk(decoded);

            case TransferFrameKind.Complete:
                return AcceptComplete(decoded);

            case TransferFrameKind.Ack:
            case TransferFrameKind.Cancel:
                return new TransferAcceptResult(true, "已确认", decoded.Kind);

            default:
                return TransferAcceptResult.Refuse("不支持的帧类型", decoded.Kind);
        }
    }

    private TransferAcceptResult AcceptManifest(TransferFrame frame)
    {
        var manifest = TransferFrames.ParseManifest(frame);
        if (manifest is null) return TransferAcceptResult.Refuse("清单无法解析", frame.Kind);

        if (manifest.Files.Count == 0) return TransferAcceptResult.Refuse("清单里没有文件", frame.Kind);
        if (manifest.Files.Count > MaxFiles) return TransferAcceptResult.Refuse($"一次最多 {MaxFiles} 个文件", frame.Kind);

        var total = manifest.Files.Sum(file => Math.Max(0, file.Size));
        if (total > MaxTotalBytes)
            return TransferAcceptResult.Refuse($"本次合计 {total} 字节，超过上限 {(long)MaxTotalBytes} 字节", frame.Kind);

        // Read the earlier attempt's map once: it decides both what is already here and which
        // existing files belong to this very transfer rather than to someone else.
        SweepStaleSessions();
        var previous = LoadState(manifest);

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index];
            if (!IsSafeName(file.Name))
                return TransferAcceptResult.Refuse($"文件名不可用：{file.Name}", frame.Kind);

            if (file.Size < 0) return TransferAcceptResult.Refuse("文件大小无效", frame.Kind);

            if (!File.Exists(Path.Combine(_destinationRoot, file.Name))) continue;

            var finishedEarlier = previous is not null && previous.TryGetValue(index, out var before) &&
                                  before.All(value => value);
            if (!finishedEarlier)
                return TransferAcceptResult.Refuse($"目标目录已有同名文件：{file.Name}", frame.Kind);
        }

        // Release the previous attempt's streams, but keep the key: this receiver continues.
        FlushState(force: true);
        foreach (var file in _files) file.Dispose();
        _files.Clear();

        Manifest = manifest;
        Resumed = previous is not null;
        _sessionDirectory = SessionDirectory(manifest.SessionId);
        Directory.CreateDirectory(_sessionDirectory);

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index];
            var chunkCount = (int)Math.Ceiling(file.Size / (double)_chunkSize);
            var tempPath = Path.Combine(_sessionDirectory, index + ".part");

            var received = previous is not null && previous.TryGetValue(index, out var bits) && bits.Length == chunkCount
                ? bits
                : new bool[chunkCount];

            // A file that had already been moved into place is done; there is nothing left to send.
            var alreadySaved = File.Exists(Path.Combine(_destinationRoot, file.Name));
            if (alreadySaved)
                Array.Fill(received, true);

            _files.Add(new IncomingFile(file, chunkCount, tempPath, received, alreadySaved)
            {
                SavedPath = alreadySaved ? Path.Combine(_destinationRoot, file.Name) : null
            });
        }

        FlushState(force: true);

        var missing = _files.Sum(file => file.ChunkCount - file.ReceivedCount);
        return new TransferAcceptResult(true,
            Resumed ? $"继续上次的传输：还缺 {missing} 个分块" : $"已接受清单：{manifest.Files.Count} 个文件",
            frame.Kind);
    }

    private TransferAcceptResult AcceptChunk(TransferFrame frame)
    {
        if (Manifest is null) return TransferAcceptResult.Refuse("还没有收到清单", frame.Kind);

        if (frame.FileIndex < 0 || frame.FileIndex >= _files.Count)
            return TransferAcceptResult.Refuse("文件序号越界", frame.Kind);

        var incoming = _files[frame.FileIndex];
        if (frame.ChunkIndex < 0 || frame.ChunkIndex >= incoming.ChunkCount)
            return TransferAcceptResult.Refuse("分块序号越界", frame.Kind);

        if (incoming.Completed) return new TransferAcceptResult(true, "该文件已完成", frame.Kind);

        try
        {
            incoming.Write(frame.ChunkIndex, frame.Payload, _chunkSize);
        }
        catch (IOException ex)
        {
            return TransferAcceptResult.Refuse($"写入失败：{ex.GetType().Name}", frame.Kind);
        }

        FlushState(force: false);
        return new TransferAcceptResult(true, "已接收分块", frame.Kind);
    }

    private TransferAcceptResult AcceptComplete(TransferFrame frame)
    {
        if (Manifest is null) return TransferAcceptResult.Refuse("还没有收到清单", frame.Kind);
        if (frame.FileIndex < 0 || frame.FileIndex >= _files.Count)
            return TransferAcceptResult.Refuse("文件序号越界", frame.Kind);

        var incoming = _files[frame.FileIndex];
        if (incoming.Completed) return new TransferAcceptResult(true, $"{incoming.File.Name} 已经保存过", frame.Kind);

        if (incoming.ReceivedCount < incoming.ChunkCount)
        {
            return TransferAcceptResult.Refuse(
                $"还缺 {incoming.ChunkCount - incoming.ReceivedCount} 个分块，不能完成", frame.Kind);
        }

        incoming.Flush();

        if (!string.Equals(TransferFrames.ComputeFileHash(incoming.TempPath), incoming.File.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            incoming.Discard();
            FlushState(force: true);
            return TransferAcceptResult.Refuse("整文件校验不通过，已丢弃", frame.Kind);
        }

        var destination = Path.Combine(_destinationRoot, incoming.File.Name);
        try
        {
            // Overwriting someone's file on the strength of a network message is not a decision this
            // layer gets to make.
            if (File.Exists(destination)) return TransferAcceptResult.Refuse($"目标文件已存在：{incoming.File.Name}", frame.Kind);
            File.Move(incoming.TempPath, destination);
        }
        catch (IOException ex)
        {
            return TransferAcceptResult.Refuse($"落盘失败：{ex.GetType().Name}", frame.Kind);
        }

        incoming.Completed = true;
        incoming.SavedPath = destination;
        FlushState(force: true);

        if (_files.All(file => file.Completed))
        {
            // Keep the record instead of deleting it: a retry of a finished transfer should be
            // recognised as already done, not mistaken for someone else taking the file name.
            FlushState(force: true);
        }
        return new TransferAcceptResult(true, $"已保存 {incoming.File.Name}", frame.Kind);
    }


    /// <summary>
    /// Reads the chunk map left by an earlier attempt, but only if it describes exactly this
    /// manifest. A mismatch starts clean rather than mixing two transfers together.
    /// </summary>
    private Dictionary<int, bool[]>? LoadState(TransferManifest manifest)
    {
        var directory = SessionDirectory(manifest.SessionId);
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path)) return null;

        ReceiverStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<ReceiverStateFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }

        if (state is null || state.SessionId != manifest.SessionId) return null;
        if (state.Files.Count != manifest.Files.Count) return null;

        var map = new Dictionary<int, bool[]>();
        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var saved = state.Files.FirstOrDefault(entry => entry.Index == index);
            var file = manifest.Files[index];
            if (saved is null || saved.Name != file.Name || saved.Size != file.Size || saved.Sha256 != file.Sha256) return null;

            var chunkCount = (int)Math.Ceiling(file.Size / (double)_chunkSize);
            map[index] = UnpackBits(saved.Received, chunkCount);

            if (!File.Exists(Path.Combine(directory, index + ".part")) && !map[index].All(value => value)) return null;
        }

        return map;
    }

    private void FlushState(bool force)
    {
        if (string.IsNullOrEmpty(_sessionDirectory) || Manifest is null) return;

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastFlush < FlushInterval) return;
        _lastFlush = now;

        var state = new ReceiverStateFile
        {
            SessionId = Manifest.SessionId,
            Files = _files.Select((file, index) => new ReceiverStateFileEntry
            {
                Index = index,
                Name = file.File.Name,
                Size = file.File.Size,
                Sha256 = file.File.Sha256,
                Received = PackBits(file.Received)
            }).ToList()
        };

        try
        {
            var path = Path.Combine(_sessionDirectory, "state.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a progress record costs a few resent chunks, not the transfer.
        }
    }

    private string SessionDirectory(string sessionId) =>
        Path.Combine(_destinationRoot, ".incoming",
            string.IsNullOrWhiteSpace(sessionId) ? "unnamed" : SanitizeSessionId(sessionId));

    private static string SanitizeSessionId(string sessionId)
    {
        // Letters, digits and the two harmless separators: an id has to be safe as a directory name
        // without changing under the caller, or a retry would look in the wrong place.
        var cleaned = new string(sessionId.Where(value => char.IsLetterOrDigit(value) || value is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "unnamed" : cleaned[..Math.Min(64, cleaned.Length)];
    }

    /// <summary>Removes session records nobody came back for, so a lost transfer cannot pile up.</summary>
    private void SweepStaleSessions()
    {
        var root = Path.Combine(_destinationRoot, ".incoming");
        if (!Directory.Exists(root)) return;

        foreach (var directory in Directory.GetDirectories(root))
        {
            try
            {
                // Never sweep the session this receiver is working on.
                if (Directory.Exists(_sessionDirectory) &&
                    string.Equals(Path.GetFullPath(directory), Path.GetFullPath(_sessionDirectory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var statePath = Path.Combine(directory, "state.json");
                var stamp = File.Exists(statePath)
                    ? File.GetLastWriteTimeUtc(statePath)
                    : Directory.GetLastWriteTimeUtc(directory);

                if (DateTime.UtcNow - stamp > StaleSessionAge) Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory we cannot remove is not worth failing the manifest over.
            }
        }
    }

    private void CleanSession()
    {
        try
        {
            if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a finished session directory behind is untidy, not incorrect.
        }
    }

    /// <summary>A name is only ever a file name here: it cannot address a directory at all.</summary>
    private static bool IsSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 200) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }

    private static string PackBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var index = 0; index < bits.Length; index++)
            if (bits[index]) bytes[index / 8] |= (byte)(1 << (index % 8));

        return Convert.ToBase64String(bytes);
    }

    private static bool[] UnpackBits(string packed, int length)
    {
        var bits = new bool[length];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(packed);
        }
        catch (FormatException)
        {
            return bits;
        }

        for (var index = 0; index < length && index / 8 < bytes.Length; index++)
            bits[index] = (bytes[index / 8] & (1 << (index % 8))) != 0;

        return bits;
    }

    public void Dispose()
    {
        FlushState(force: true);
        foreach (var file in _files) file.Dispose();
        _files.Clear();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_groupKey);
    }

    private sealed class IncomingFile : IDisposable
    {
        private FileStream? _stream;

        public IncomingFile(TransferFile file, int chunkCount, string tempPath, bool[] received, bool completed)
        {
            File = file;
            ChunkCount = chunkCount;
            TempPath = tempPath;
            Received = received;
            Completed = completed;
            ReceivedCount = received.Count(value => value);
        }

        public TransferFile File { get; }

        public int ChunkCount { get; }

        public string TempPath { get; }

        public bool[] Received { get; }

        public int ReceivedCount { get; private set; }

        public bool Completed { get; set; }

        public string? SavedPath { get; set; }

        public void Write(int chunkIndex, ReadOnlySpan<byte> payload, int chunkSize)
        {
            _stream ??= new FileStream(TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

            if (!Received[chunkIndex])
            {
                Received[chunkIndex] = true;
                ReceivedCount++;
            }

            _stream.Seek((long)chunkIndex * chunkSize, SeekOrigin.Begin);
            _stream.Write(payload);
            _stream.Flush();
        }

        public void Flush()
        {
            _stream?.Flush(true);
            _stream?.Dispose();
            _stream = null;
        }

        public void Discard()
        {
            Flush();
            // The File property below names the announced file, so the IO type needs saying in full.
            try { System.IO.File.Delete(TempPath); } catch (IOException) { /* best effort */ }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed class ReceiverStateFile
    {
        public string SessionId { get; set; } = string.Empty;

        public List<ReceiverStateFileEntry> Files { get; set; } = [];
    }

    private sealed class ReceiverStateFileEntry
    {
        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string Received { get; set; } = string.Empty;
    }
}
