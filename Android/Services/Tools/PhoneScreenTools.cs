using System.Globalization;
using System.Text.Json.Nodes;
using FairyAI_Android.Services.Screen;

namespace FairyAI_Android.Services.Tools;

/// <summary>
/// The phone's own screen capabilities as tools. Every call goes through <see cref="ScreenGate"/> —
/// the whitelist, the target policy, the per-capability grant and a fresh approval — so a model asking
/// for a tap gets the same treatment a user tapping the button would.
/// </summary>
public sealed class PhoneScreenTool : ITool
{
    private readonly Func<ScreenAction, string?, Task<ScreenDecision>> _run;
    private readonly ScreenCapability _capability;

    /// <summary>
    /// Takes the gate's run delegate rather than the whole service: the tool only needs a decision, and
    /// that keeps the argument/refusal mapping testable without Android.
    /// </summary>
    public PhoneScreenTool(Func<ScreenAction, string?, Task<ScreenDecision>> run, ScreenCapability capability)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _capability = capability;
    }

    public string Name => "phone_" + _capability.ToString().ToLowerInvariant();

    public string Description => _capability switch
    {
        ScreenCapability.Tap => "在这台手机的屏幕上点击一个坐标（需要 Shizuku 授权，每次都会先问用户）",
        ScreenCapability.Swipe => "在这台手机的屏幕上从一点滑动到另一点",
        ScreenCapability.TypeText => "向当前输入框输入一段文字（会先问用户，支付/密码类字样一律拒绝）",
        ScreenCapability.PressKey => "按一个安卓按键，例如 KEYCODE_BACK 或 4",
        ScreenCapability.ReadScreen => "读取当前屏幕焦点窗口（只返回一行窗口信息，不截图）",
        _ => "手机屏幕操作",
    };

    public List<ToolParameter> Parameters => _capability switch
    {
        ScreenCapability.Tap => new List<ToolParameter>
        {
            new() { Name = "x", Type = "integer", Description = "横坐标像素" },
            new() { Name = "y", Type = "integer", Description = "纵坐标像素" },
            new() { Name = "app_package", Type = "string", Description = "目标应用包名（已知时填，金融类会被拒绝）", Required = false },
        },
        ScreenCapability.Swipe => new List<ToolParameter>
        {
            new() { Name = "x", Type = "integer", Description = "起点横坐标" },
            new() { Name = "y", Type = "integer", Description = "起点纵坐标" },
            new() { Name = "x2", Type = "integer", Description = "终点横坐标" },
            new() { Name = "y2", Type = "integer", Description = "终点纵坐标" },
            new() { Name = "app_package", Type = "string", Description = "目标应用包名（已知时填）", Required = false },
        },
        ScreenCapability.TypeText => new List<ToolParameter>
        {
            new() { Name = "text", Type = "string", Description = "要输入的文字" },
            new() { Name = "app_package", Type = "string", Description = "目标应用包名（已知时填）", Required = false },
        },
        ScreenCapability.PressKey => new List<ToolParameter>
        {
            new() { Name = "key", Type = "string", Description = "数字或 KEYCODE_ 名称，例如 KEYCODE_BACK" },
        },
        ScreenCapability.ReadScreen => new List<ToolParameter>(),
        _ => new List<ToolParameter>(),
    };

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var action = new ScreenAction(
            _capability,
            X: ReadInt(args, "x"),
            Y: ReadInt(args, "y"),
            X2: ReadInt(args, "x2"),
            Y2: ReadInt(args, "y2"),
            Text: args?["text"]?.GetValue<string>(),
            Key: args?["key"]?.GetValue<string>());

        var package = args?["app_package"]?.GetValue<string>();
        var decision = await _run(action, package).ConfigureAwait(false);

        return decision.Allowed
            ? string.IsNullOrWhiteSpace(decision.Message) ? "已执行" : decision.Message
            : $"未执行（{decision.Reason}）：{decision.Message}";
    }

    private static int ReadInt(JsonObject? args, string name)
    {
        var node = args?[name];
        if (node is null) return 0;

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
                System.Text.Json.JsonValueKind.String => int.TryParse(
                    node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0,
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }
}
