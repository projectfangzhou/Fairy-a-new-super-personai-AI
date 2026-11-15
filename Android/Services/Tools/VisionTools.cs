using System.Text.Json.Nodes;
using FairyAI_Android.Services.Vision;

namespace FairyAI_Android.Services.Tools;

/// <summary>
/// Lets the model look at the phone's screen. The capture still needs the user's per-action approval
/// (asked by the screen gate, with a dialog that says the picture goes to a vision model), so this
/// tool can be refused at two levels and both refusals come back as readable text.
/// </summary>
public sealed class DescribeScreenTool : ITool
{
    private readonly Func<string?, Task<VisionOutcome>> _look;

    public DescribeScreenTool(Func<string?, Task<VisionOutcome>> look) =>
        _look = look ?? throw new ArgumentNullException(nameof(look));

    public string Name => "describe_screen";

    public string Description => "看一眼手机当前屏幕并用视觉模型描述；需要屏幕读取授权和一次审批";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "question", Type = "string", Description = "想知道屏幕上的什么，可留空", Required = false },
    };

    public async Task<string> ExecuteAsync(JsonObject args)
    {
        var question = args?["question"]?.GetValue<string>();
        var outcome = await _look(question).ConfigureAwait(false);

        return outcome.Message;
    }
}
