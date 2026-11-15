using System.Text;
using System.Text.Json;

namespace FairyAI_Android.Services.Vision;

/// <summary>
/// Builds the OpenAI-compatible multimodal request and the prompt that goes with it.
///
/// The MIME type is read from the bytes rather than assumed: an earlier service in this project
/// hard-coded image/jpeg while the capture produced PNG, which some providers reject.
/// </summary>
public static class VisionRequest
{
    public const int DefaultMaxTokens = 900;

    private const string SystemlessPrompt =
        "你在看一台手机的屏幕截图。请只用中文回答。\n" +
        "只描述图里确实存在的内容：看不清就说看不清，不要猜、不要补全。\n" +
        "如果有关键信息（时间、地点、金额、电话、按钮名称、报错文字），照原样写出来。\n";

    /// <summary>The prompt for one look. A user question is appended, not replaced.</summary>
    public static string BuildPrompt(string? question)
    {
        var trimmed = (question ?? "").Trim();
        if (trimmed.Length == 0)
            return SystemlessPrompt + "请简要说明这一屏是什么应用或页面，以及上面有哪些主要内容。";

        return SystemlessPrompt + "用户想知道：" + trimmed;
    }

    public static string MimeOf(byte[] image)
    {
        if (image is null || image.Length < 4) return "image/png";

        if (image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47) return "image/png";
        if (image[0] == 0xFF && image[1] == 0xD8) return "image/jpeg";
        if (image[0] == 0x47 && image[1] == 0x49 && image[2] == 0x46) return "image/gif";
        if (image[0] == 0x52 && image[1] == 0x49 && image[2] == 0x46 && image[3] == 0x46) return "image/webp";

        return "image/png";
    }

    public static string DataUri(byte[] image) =>
        $"data:{MimeOf(image)};base64,{Convert.ToBase64String(image ?? Array.Empty<byte>())}";

    public static string BuildBody(VisionTarget target, string prompt, byte[] image, int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(target);

        var payload = new
        {
            model = target.Model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = DataUri(image) } },
                    },
                },
            },
            max_tokens = maxTokens,
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>The request body as bytes; kept separate so a test can assert on valid JSON.</summary>
    public static StringContent BuildContent(VisionTarget target, string prompt, byte[] image, int maxTokens = DefaultMaxTokens) =>
        new(BuildBody(target, prompt, image, maxTokens), Encoding.UTF8, "application/json");
}
