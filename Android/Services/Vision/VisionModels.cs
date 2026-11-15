namespace FairyAI_Android.Services.Vision;

/// <summary>The vision provider as configured in the app. Plain values, so the choice can be tested.</summary>
public sealed record VisionSettings(string Provider, string BaseUrl, string Model, string ApiKey)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// Set once a probe has shown this endpoint can actually see an image. For the app's own vision
    /// block it is informational; for the main model it is what makes it eligible as a fallback.
    /// </summary>
    public bool SeesImages { get; init; }
}

/// <summary>The custom-platform multimodal entry, which can stand in for a vision model.</summary>
public sealed record MultimodalSettings(bool Enabled, string BaseUrl, string Model, string ApiKey)
{
    public bool IsUsable =>
        Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Which endpoint the app will actually call, and under which name it reports it.</summary>
public sealed record VisionTarget(string Name, string BaseUrl, string Model, string ApiKey);

/// <summary>
/// Picks the vision endpoint, in the order the user would expect: the vision block they filled in,
/// then the custom-platform multimodal block, then — only if a probe has proven it can see images —
/// the main model itself.
///
/// That last one is why nothing here looks at model names. "Is this model omnimodal" cannot be read
/// off a name: the same family ships vision and text models, and names mean different things across
/// vendors. The only reliable answer comes from <see cref="VisionProbe"/> actually showing it a
/// picture, and this class just consumes that result.
/// </summary>
public static class VisionTargetSelector
{
    public const string MissingReason =
        "还没有可用的视觉模型：到设置里填视觉模型的接口地址和模型名称，或者打开自定义平台里的多模态，" +
        "点「测一下能不能看图」确认主模型也能看图后，识屏会直接用主模型。";

    public static (VisionTarget? Target, string Reason) Select(
        VisionSettings? vision,
        MultimodalSettings? custom,
        VisionSettings? mainModel = null)
    {
        if (vision is not null && vision.IsUsable)
        {
            var name = string.IsNullOrWhiteSpace(vision.Provider) ? "视觉模型" : vision.Provider.Trim();
            return (new VisionTarget(name, vision.BaseUrl.Trim(), vision.Model.Trim(), vision.ApiKey ?? ""), "");
        }

        if (custom is not null && custom.IsUsable)
            return (new VisionTarget("自定义多模态", custom.BaseUrl.Trim(), custom.Model.Trim(), custom.ApiKey ?? ""), "");

        if (mainModel is not null && mainModel.IsUsable && mainModel.SeesImages)
        {
            var name = string.IsNullOrWhiteSpace(mainModel.Provider) ? "主模型" : "主模型 " + mainModel.Provider.Trim();
            return (new VisionTarget(name, mainModel.BaseUrl.Trim(), mainModel.Model.Trim(), mainModel.ApiKey ?? ""), "");
        }

        return (null, MissingReason);
    }
}

/// <summary>What the vision endpoint answered. <see cref="ImageBytes"/> is what was actually sent.</summary>
public sealed record VisionAnswer(bool Ok, string Text, string? Error, int ImageBytes)
{
    public static VisionAnswer Fail(string error, int imageBytes = 0) => new(false, "", error, imageBytes);
}

/// <summary>
/// The whole look-at-the-screen flow: where the picture came from, what the model said, and which
/// details were pulled out of the answer. <see cref="Capture"/> is always filled in, even on failure,
/// so the user can tell "no screenshot" apart from "no vision model".
/// </summary>
public sealed record VisionOutcome(
    bool Ok,
    string Message,
    string? Answer,
    string Capture,
    IReadOnlyList<string> Extracted)
{
    public static VisionOutcome Fail(string capture, string message) =>
        new(false, message, null, capture, Array.Empty<string>());
}
