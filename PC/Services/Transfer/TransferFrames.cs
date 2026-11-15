using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.Transfer;

public enum TransferFrameKind : byte
{
    Manifest = 1,
    Chunk = 2,
    Ack = 3,
    Complete = 4,
    Cancel = 5
}

/// <summary>
/// The receiver's final word: these files are on disk. A sender waits for it before calling the
/// transfer done, so "sent" never means "pushed some frames and hoped".
/// </summary>
public sealed record TransferCompletion(string SessionId, IReadOnlyList<string> Saved)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static TransferCompletion? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return null;

        try
        {
            var completion = JsonSerializer.Deserialize<TransferCompletion>(payload);
            return completion is { Saved.Count: > 0 } ? completion : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
public sealed record TransferFrame(TransferFrameKind Kind, int FileIndex, int ChunkIndex, byte[] Payload);

/// <summary>What a receiver is still missing in one file of a resumed transfer.</summary>
public sealed record TransferFileResume(int FileIndex, IReadOnlyList<int> MissingChunks);

/// <summary>
/// A resume plan: what the receiver already holds, so a retry sends only what is absent.
///
/// A retry continues the same transfer rather than starting a new one — same session id, same
/// destination directory — which is why the plan is addressed by session id and not by file names.
/// </summary>
public sealed record TransferResumePlan(string SessionId, IReadOnlyList<TransferFileResume> Files)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static TransferResumePlan? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return null;

        try
        {
            return JsonSerializer.Deserialize<TransferResumePlan>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Chunks still needed for one file; all chunks when the plan says nothing about it.</summary>
    public IReadOnlyList<int> MissingFor(int fileIndex, int chunkCount)
    {
        var entry = Files.FirstOrDefault(file => file.FileIndex == fileIndex);
        return entry is null ? Enumerable.Range(0, chunkCount).ToArray() : entry.MissingChunks;
    }

    public bool IsComplete(int fileIndex) => Files.Any(file => file.FileIndex == fileIndex && file.MissingChunks.Count == 0);
}


/// <summary>One file as announced before any byte of it is sent.</summary>
public sealed record TransferFile(string Name, long Size, string Sha256);

public sealed record TransferManifest(string SessionId, IReadOnlyList<TransferFile> Files, string Sender);

/// <summary>
/// The wire format for file bytes.
///
/// Every frame is encrypted and authenticated with the group key, and the header travels as
/// associated data, so a receiver cannot be tricked into accepting a frame that was re-labelled as
/// a different file or chunk. There is no plaintext mode: an unpaired peer has no key, which is the
/// simplest way to say that it also has no access.
/// </summary>
public static class TransferFrames
{
    public const int MaxPayloadBytes = 64 * 1024;
    public const byte Version = 1;

    private const int HeaderBytes = 28;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] Magic = [0x46, 0x4E];

    public static byte[] Encode(TransferFrameKind kind, int fileIndex, int chunkIndex,
        ReadOnlySpan<byte> payload, byte[] groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));
        if (payload.Length > MaxPayloadBytes) throw new ArgumentException("帧负载过大", nameof(payload));

        var frame = new byte[HeaderBytes + payload.Length + TagBytes];
        Magic.CopyTo(frame, 0);
        frame[2] = Version;
        frame[3] = (byte)kind;
        BitConverter.TryWriteBytes(frame.AsSpan(4), fileIndex);
        BitConverter.TryWriteBytes(frame.AsSpan(8), chunkIndex);
        BitConverter.TryWriteBytes(frame.AsSpan(12), payload.Length);
        RandomNumberGenerator.Fill(frame.AsSpan(16, NonceBytes));

        using var aes = new AesGcm(groupKey, TagBytes);
        aes.Encrypt(frame.AsSpan(16, NonceBytes), payload, frame.AsSpan(HeaderBytes, payload.Length),
            frame.AsSpan(HeaderBytes + payload.Length, TagBytes), frame.AsSpan(0, HeaderBytes));

        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, byte[] groupKey, out TransferFrame? decoded, out string error)
    {
        decoded = null;

        if (groupKey.Length != 32)
        {
            error = "组密钥长度不对";
            return false;
        }

        if (frame.Length < HeaderBytes + TagBytes)
        {
            error = "帧过短";
            return false;
        }

        if (frame[0] != Magic[0] || frame[1] != Magic[1])
        {
            error = "不是仙灵传输帧";
            return false;
        }

        if (frame[2] != Version)
        {
            error = $"不支持的帧版本 {frame[2]}";
            return false;
        }

        var kind = (TransferFrameKind)frame[3];
        if (!Enum.IsDefined(kind))
        {
            error = $"未知的帧类型 {frame[3]}";
            return false;
        }

        var fileIndex = BitConverter.ToInt32(frame[4..8]);
        var chunkIndex = BitConverter.ToInt32(frame[8..12]);
        var length = BitConverter.ToInt32(frame[12..16]);

        if (length < 0 || length > MaxPayloadBytes || frame.Length != HeaderBytes + length + TagBytes)
        {
            error = "帧长度与声明不一致";
            return false;
        }

        var payload = new byte[length];
        try
        {
            using var aes = new AesGcm(groupKey, TagBytes);
            aes.Decrypt(frame[16..28], frame.Slice(HeaderBytes, length),
                frame.Slice(HeaderBytes + length, TagBytes), payload, frame[..HeaderBytes]);
        }
        catch (CryptographicException)
        {
            // Wrong key or modified bytes land here; neither is recoverable, and neither is reported
            // as anything other than a refusal.
            CryptographicOperations.ZeroMemory(payload);
            error = "帧校验失败（密钥不符或内容被改动）";
            return false;
        }

        decoded = new TransferFrame(kind, fileIndex, chunkIndex, payload);
        error = string.Empty;
        return true;
    }

    public static byte[] EncodeManifest(TransferManifest manifest, byte[] groupKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (json.Length > MaxPayloadBytes) throw new InvalidOperationException("清单过大，文件数量太多");

        return Encode(TransferFrameKind.Manifest, -1, -1, json, groupKey);
    }

    public static TransferManifest? ParseManifest(TransferFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Kind != TransferFrameKind.Manifest) return null;

        try
        {
            return JsonSerializer.Deserialize<TransferManifest>(frame.Payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// Turns local files into the frames a channel carries.
///
/// It reads one chunk at a time on purpose: the whole point of the format is that neither side ever
/// holds a large file in memory, and that an interrupted transfer can pick up at a known chunk
/// instead of starting over.
/// </summary>
public sealed class TransferSender
{
    private readonly byte[] _groupKey;
    private readonly IReadOnlyList<string> _paths;
    private readonly int _chunkSize;

    /// <param name="sessionId">
    /// The identity of this transfer. A retry passes the id of the attempt it continues, so the
    /// receiver can answer with what it already holds instead of receiving everything again.
    /// </param>
    public TransferSender(byte[] groupKey, IReadOnlyList<string> paths, string sender,
        int chunkSize = TransferFrames.MaxPayloadBytes, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (groupKey.Length != 32) throw new ArgumentException("组密钥长度必须是 32 字节", nameof(groupKey));
        if (paths is null || paths.Count == 0) throw new ArgumentException("没有要发送的文件", nameof(paths));
        if (chunkSize is < 1024 or > TransferFrames.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "分块大小需在 1KB 到 64KB 之间");

        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在：" + Path.GetFileName(path), path);
        }

        _groupKey = groupKey;
        _paths = paths;
        _chunkSize = chunkSize;

        var files = paths.Select(path =>
        {
            var info = new FileInfo(path);
            return new TransferFile(Path.GetFileName(path), info.Length, TransferFrames.ComputeFileHash(path));
        }).ToArray();

        Manifest = new TransferManifest(
            string.IsNullOrWhiteSpace(sessionId)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
                : sessionId,
            files,
            string.IsNullOrWhiteSpace(sender) ? "未命名设备" : sender.Trim());
    }

    public TransferManifest Manifest { get; }

    public int ChunkSize => _chunkSize;

    public int ChunkCount(int fileIndex) =>
        (int)Math.Ceiling(Manifest.Files[fileIndex].Size / (double)_chunkSize);

    public byte[] BuildManifestFrame() => TransferFrames.EncodeManifest(Manifest, _groupKey);

    /// <summary>Frames for one file, starting at <paramref name="fromChunk"/> so a resume skips what the peer has.</summary>
    public IEnumerable<byte[]> BuildChunkFrames(int fileIndex, int fromChunk = 0)
    {
        if (fileIndex < 0 || fileIndex >= Manifest.Files.Count)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        var buffer = new byte[_chunkSize];
        using var stream = File.OpenRead(_paths[fileIndex]);
        stream.Seek((long)Math.Max(0, fromChunk) * _chunkSize, SeekOrigin.Begin);

        var index = Math.Max(0, fromChunk);
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) yield break;

            yield return TransferFrames.Encode(TransferFrameKind.Chunk, fileIndex, index,
                buffer.AsSpan(0, read), _groupKey);
            index++;
        }
    }

    /// <summary>One specific chunk, for a resume that must skip what the receiver already has.</summary>
    public byte[]? BuildChunkFrame(int fileIndex, int chunkIndex)
    {
        if (fileIndex < 0 || fileIndex >= Manifest.Files.Count)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        if (chunkIndex < 0 || chunkIndex >= ChunkCount(fileIndex)) return null;

        var payload = new byte[_chunkSize];
        using var stream = File.OpenRead(_paths[fileIndex]);
        stream.Seek((long)chunkIndex * _chunkSize, SeekOrigin.Begin);

        var read = stream.Read(payload, 0, payload.Length);
        if (read == 0) return null;

        return TransferFrames.Encode(TransferFrameKind.Chunk, fileIndex, chunkIndex, payload.AsSpan(0, read), _groupKey);
    }
    public byte[] BuildCompleteFrame(int fileIndex) =>
        TransferFrames.Encode(TransferFrameKind.Complete, fileIndex, -1, ReadOnlySpan<byte>.Empty, _groupKey);

    public byte[] BuildAckFrame(string text) =>
        TransferFrames.Encode(TransferFrameKind.Ack, -1, -1, Encoding.UTF8.GetBytes(text), _groupKey);
}
