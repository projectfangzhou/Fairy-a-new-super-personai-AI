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
///
/// <see cref="AccentData"/> is the filled part of the same icon — the dots, sliders and
/// pivots that should read as solid rather than outlined. Splitting them is what makes the
/// 「描边 + 实心」 hybrid possible: WPF's Path cannot stroke one sub-path and fill another
/// in a single element.
/// </summary>
public sealed record DockEntry(
    DockAction Action,
    string Title,
    string IconKey,
    string IconData,
    bool IsEnabled,
    string? UnavailableReason,
    string? AccentData = null);

public static class DockCatalog
{
    public static IReadOnlyList<DockEntry> Entries { get; } =
    [
        // 控制中心：仪表盘 —— 扇形刻度 + 实心指针
        new(DockAction.ControlCenter, "控制中心", "control-center",
            "M12,4 A8,8 0 0 1 20,12 M12,4 A8,8 0 0 0 12,20 A8,8 0 0 1 4,12 M12,12 L16.5,8.5",
            true, null,
            "M12,12 m-1.5,0 a1.5,1.5 0 1,0 3,0 a1.5,1.5 0 1,0 -3,0"),

        // 聊天：气泡轮廓 + 三个实心点
        new(DockAction.Chat, "聊天", "chat",
            "M4,5.5 H20 V15 H9.5 L5,18.5 V15 H4 Z",
            true, null,
            "M8.5,10 m-1.2,0 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0 " +
            "M12,10 m-1.2,0 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0 " +
            "M15.5,10 m-1.2,0 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0"),

        // 终端：尖括号 + 实心光标块
        new(DockAction.Terminal, "终端", "terminal",
            "M5,7 L9,12 L5,17 M11.5,17 H19",
            false, "尚未实现 · 计划中",
            "M12.5,7 L17.5,7 L17.5,11 L12.5,11 Z"),

        // Fairy Code：代码括号 + 中间实心条
        new(DockAction.FairyCode, "Fairy Code", "fairy-code",
            "M9,6 L4,12 L9,18 M15,6 L20,12 L15,18",
            true, null,
            "M11.3,8 L11.3,16 L12.7,16 L12.7,8 Z"),

        // 模型库：立方体线框 + 实心顶点
        new(DockAction.ModelLibrary, "模型库", "model-library",
            "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M4,7.5 L12,12 L20,7.5 M12,12 V21",
            true, null,
            "M12,12 m-1.7,0 a1.7,1.7 0 1,0 3.4,0 a1.7,1.7 0 1,0 -3.4,0"),

        // 设置：三条滑轨 + 实心滑块
        new(DockAction.Settings, "设置", "settings",
            "M4,7 H20 M4,12 H20 M4,17 H20",
            false, "请在控制中心内修改设置",
            "M8.5,7 m-2.3,0 a2.3,2.3 0 1,0 4.6,0 a2.3,2.3 0 1,0 -4.6,0 " +
            "M15.5,12 m-2.3,0 a2.3,2.3 0 1,0 4.6,0 a2.3,2.3 0 1,0 -4.6,0 " +
            "M6.5,17 m-2.3,0 a2.3,2.3 0 1,0 4.6,0 a2.3,2.3 0 1,0 -4.6,0")
    ];

    public static DockEntry Find(DockAction action) => Entries.First(e => e.Action == action);
}
