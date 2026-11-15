using System.IO;
using System.Text.RegularExpressions;

namespace MyAiAssistant.Services.Pairing;

/// <summary>Which kind of data a group keeps apart from other groups.</summary>
public enum GroupSpace
{
    Files = 0,
    Config,
    Commands,
    Relay,
    Knowledge
}

/// <summary>
/// The per-group namespace.
///
/// Several groups can share one machine and one relay address, so every piece of data a group owns
/// has to sit somewhere the other groups cannot reach by accident. Two groups therefore never
/// produce the same directory or the same configuration key, and a name that tries to climb out of
/// its own directory is rejected instead of normalised into a path that belongs to someone else.
/// </summary>
public sealed class GroupIsolation
{
    private static readonly Regex GroupIdPattern = new("^[A-Z0-9]{6,48}$", RegexOptions.Compiled);

    public GroupIsolation(string root, string groupId)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("根目录不能为空", nameof(root));
        if (string.IsNullOrWhiteSpace(groupId) || !GroupIdPattern.IsMatch(groupId))
            throw new ArgumentException("设备组编号格式不正确", nameof(groupId));

        BaseRoot = Path.GetFullPath(root);
        GroupId = groupId;
        Root = Path.Combine(BaseRoot, "groups", groupId);
    }

    public string BaseRoot { get; }

    public string GroupId { get; }

    /// <summary>Everything this group owns lives under here, and nothing else does.</summary>
    public string Root { get; }

    public static string SpaceName(GroupSpace space) => space switch
    {
        GroupSpace.Files => "files",
        GroupSpace.Config => "config",
        GroupSpace.Commands => "commands",
        GroupSpace.Relay => "relay",
        GroupSpace.Knowledge => "knowledge",
        _ => space.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// A path inside the space. <paramref name="name"/> must be a plain file name: anything that
    /// resolves to a different directory is refused rather than silently moved.
    /// </summary>
    public string PathFor(GroupSpace space, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("名称包含非法字符", nameof(name));

        var directory = Path.Combine(Root, SpaceName(space));
        var full = Path.GetFullPath(Path.Combine(directory, trimmed));

        if (!full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("名称超出本设备组的目录", nameof(name));

        return full;
    }

    /// <summary>A configuration key that cannot collide with the same key in another group.</summary>
    public string KeyFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("键不能为空", nameof(key));
        return $"g:{GroupId}:{key.Trim()}";
    }

    public string EnsureDirectory(GroupSpace space)
    {
        var directory = Path.Combine(Root, SpaceName(space));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
