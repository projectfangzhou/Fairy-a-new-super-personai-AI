using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MyAiAssistant.Services.LocalSend;

/// <summary>What a sender asked to send us, shown to the user before anything is written.</summary>
public sealed record LocalSendIncoming(
    string SenderAlias,
    string SenderFingerprint,
    IReadOnlyList<LocalSendFile> Files)
{
    public long TotalBytes => Files.Sum(file => file.Size);
}

public sealed record LocalSendReceiveSession(
    string SessionId,
    string SenderAlias,
    string SenderFingerprint,
    IReadOnlyDictionary<string, LocalSendFile> Files,
    IReadOnlyDictionary<string, string> Tokens,
    DateTimeOffset CreatedAt)
{
    public long ExpectedBytes => Files.Values.Sum(file => file.Size);
}

/// <summary>
/// Names received files can be saved under. A sender controls this string, so it is treated as
/// hostile: no separators, no traversal, no reserved device names, and a length cap.
/// </summary>
public static class LocalSendFileNaming
{
    private const int MaxLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Returns a safe file name, or null when nothing safe is left. Callers must treat null as
    /// "refuse this file", never as "use a default name".
    /// </summary>
    public static string? Sanitize(string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return null;

        // Take the last path segment under either separator, so "../../x" cannot escape.
        var name = supplied.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '\0' or '\r' or '\n' or '\t') continue;
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..") return null;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (Reserved.Contains(stem)) name = "_" + name;

        if (name.Length > MaxLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 16) extension = string.Empty;
            var keep = Math.Max(1, MaxLength - extension.Length);
            name = name[..Math.Min(keep, name.Length)] + extension;
        }

        return name.Length == 0 ? null : name;
    }

    /// <summary>Adds a numeric suffix when the name is already taken, without overwriting.</summary>
    public static string MakeUnique(string directory, string name)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; index < 1000; index++)
        {
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
            candidate = $"{stem} ({index}){extension}";
        }
        return $"{stem}-{Guid.NewGuid():N}{extension}";
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
