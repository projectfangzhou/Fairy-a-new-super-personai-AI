namespace MyAiAssistant.ViewModels;

public enum DockAction
{
    ControlCenter = 0,
    Chat,
    Terminal,
    FairyCode,
    ModelLibrary,
    Settings
}

/// <summary>
/// One desktop-dock entry. <see cref="IconData"/> is a stroked vector path (24x24) rather than
/// a glyph or an emoji, so the rail stays line-art and matches any DPI.
/// </summary>
public sealed record DockEntry(
    DockAction Action,
    string Title,
    string IconKey,
    string IconData,
    bool IsEnabled,
    string? UnavailableReason);

public static class DockCatalog
{
    public static IReadOnlyList<DockEntry> Entries { get; } =
    [
        new(DockAction.ControlCenter, "控制中心", "control-center",
            "M4,18 A8,8 0 0 1 20,18 M12,18 L16,11 M11,18 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0",
            true, null),
        new(DockAction.Chat, "聊天", "chat",
            "M4,5 H20 V16 H10 L5,20 V16 H4 Z",
            true, null),
        new(DockAction.Terminal, "终端", "terminal",
            "M5,7 L9,12 L5,17 M12,17 H19",
            false, "尚未实现 · 计划中"),
        new(DockAction.FairyCode, "Fairy Code", "fairy-code",
            "M9,7 L4,12 L9,17 M15,7 L20,12 L15,17",
            true, null),
        new(DockAction.ModelLibrary, "模型库", "model-library",
            "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M4,7.5 L12,12 L20,7.5 M12,12 V21",
            false, "尚未实现 · 计划中"),
        new(DockAction.Settings, "设置", "settings",
            "M4,7 H20 M4,12 H20 M4,17 H20 M7,7 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0 M13,12 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0 M5,17 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0",
            false, "请在控制中心内修改设置")
    ];

    public static DockEntry Find(DockAction action) => Entries.First(e => e.Action == action);
}