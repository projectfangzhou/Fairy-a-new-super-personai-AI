namespace FairyAI_Android.Services.Screen;

/// <summary>
/// The only screen interactions this app is allowed to perform on the phone itself.
///
/// This list is a whitelist, not a sample: nothing outside it can be requested, even if a future
/// Shizuku binding would make it technically possible. Deliberately absent — changing system
/// settings, silent package installs, background privilege escalation, whole-disk file access.
/// Those are exactly what the product rules forbid, so they have no enum member to reach for.
/// </summary>
public enum ScreenCapability
{
    Tap,
    Swipe,
    TypeText,
    PressKey,
    ReadScreen,
}

public static class ScreenCapabilityCatalog
{
    /// <summary>Every capability the phone may request. Order is the order shown to the user.</summary>
    public static readonly IReadOnlyList<ScreenCapability> All = new[]
    {
        ScreenCapability.Tap,
        ScreenCapability.Swipe,
        ScreenCapability.TypeText,
        ScreenCapability.PressKey,
        ScreenCapability.ReadScreen,
    };

    public static bool IsWhitelisted(ScreenCapability capability) => All.Contains(capability);

    public static string Label(ScreenCapability capability) => capability switch
    {
        ScreenCapability.Tap => "点击",
        ScreenCapability.Swipe => "滑动",
        ScreenCapability.TypeText => "输入文字",
        ScreenCapability.PressKey => "按键",
        ScreenCapability.ReadScreen => "读取屏幕",
        _ => capability.ToString(),
    };

    /// <summary>
    /// Reads a capability out of stored configuration. Unknown names are dropped rather than coerced:
    /// a config file edited by hand must not be able to invent a capability.
    /// </summary>
    public static ScreenCapability? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (var capability in All)
        {
            if (string.Equals(capability.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return capability;
        }

        return null;
    }

    public static IReadOnlySet<ScreenCapability> ParseAll(IEnumerable<string>? names)
    {
        var set = new HashSet<ScreenCapability>();
        if (names is null) return set;

        foreach (var name in names)
        {
            var capability = Parse(name);
            if (capability is not null) set.Add(capability.Value);
        }

        return set;
    }
}
