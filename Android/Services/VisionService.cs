using FairyAI_Android.Services.Knowledge;
using FairyAI_Android.Services.Screen;
using FairyAI_Android.Services.Vision;

namespace FairyAI_Android.Services;

/// <summary>
/// Looks at the screen: capture, ask the vision model, and keep whatever details are worth keeping.
///
/// The capture is a screen action like any other, so it goes through <see cref="ScreenControl"/> —
/// which means it needs the read-screen grant, a running Shizuku, and a fresh approval that says the
/// picture goes to a vision model. When that path is unavailable the app falls back to capturing its
/// own window (no permission needed) and <em>says so</em>; it never presents its own UI as "your
/// screen". An explicit "no" in the approval dialog is not a reason to try another way in.
/// </summary>
public sealed class VisionService
{
    private readonly ScreenControl _screen;
    private readonly VisionClient _client;
    private readonly ImportantInfoService _important;

    public VisionService(ScreenControl screen, VisionClient client, ImportantInfoService important)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _important = important ?? throw new ArgumentNullException(nameof(important));
    }

    public async Task<VisionOutcome> LookAsync(string? question = null, CancellationToken cancellationToken = default)
    {
        // Check the model before touching the screen: with no vision endpoint the answer is known
        // already, so there is no reason to ask the user to approve a capture.
        if (_client.ResolveTarget() is null)
            return VisionOutcome.Fail("", VisionTargetSelector.MissingReason);

        var (bytes, capture, captureFailed) = await CaptureAsync(cancellationToken).ConfigureAwait(false);

        if (bytes is null)
            return VisionOutcome.Fail(capture, captureFailed ?? "没有拿到截图");

        var answer = await _client.DescribeAsync(bytes, question, cancellationToken).ConfigureAwait(false);

        // The capture is always part of a failure message: "the model is unreachable" and "the screen
        // could not be captured" are different problems, and which picture was used matters when the
        // answer comes from the app's own window instead of the screen.
        if (!answer.Ok)
            return VisionOutcome.Fail(capture, $"截到了（{capture}），但{answer.Error}");

        var extracted = new List<string>();

        try
        {
            extracted.AddRange(ImportantInfoExtractor.Extract(answer.Text));

            // Dates, phone numbers and addresses that showed up on screen are exactly what the
            // knowledge folder is for; one dated file per day keeps them findable later.
            if (extracted.Count > 0) await _important.ExtractAndSaveAsync(answer.Text).ConfigureAwait(false);
        }
        catch (IOException)
        {
            extracted.Clear();
        }

        var message = $"看了这一屏（{capture}）：\n{answer.Text}";
        if (extracted.Count > 0)
            message += $"\n\n另外记下了 {extracted.Count} 条日期/电话/地址到知识库。";

        return new VisionOutcome(true, message, answer.Text, capture, extracted);
    }

    private async Task<(byte[]? Bytes, string Capture, string? Failure)> CaptureAsync(CancellationToken cancellationToken)
    {
        var decision = await _screen.CaptureAsync(cancellationToken).ConfigureAwait(false);

        if (decision.Allowed && decision.Payload is { Length: > 0 } full)
            return (full, $"全屏截图 {Kilo(full.Length)}", null);

        var refused = decision.Allowed
            ? "截屏没有返回画面"
            : decision.Message;

        // The user saying no in this action's own dialog is final.
        if (!decision.Allowed &&
            decision.Reason is ScreenRefusal.ApprovalDenied or ScreenRefusal.ApprovalMissing)
            return (null, refused, "你拒绝了这次截屏，所以没有看屏幕。");

        // Everything else means the phone-wide path is not available; look at our own window instead
        // and label it as exactly that.
        var own = await CaptureOwnWindowAsync(cancellationToken).ConfigureAwait(false);
        if (own is null)
            return (null, refused, refused);

        return (own, $"只截到仙灵自己的窗口 {Kilo(own.Length)}（全屏需要：{refused}）", null);
    }

    /// <summary>MAUI's own screenshot: no permission, but it only sees this app's window.</summary>
    private static async Task<byte[]?> CaptureOwnWindowAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Screenshot.Default.IsCaptureSupported) return null;

            var shot = await Screenshot.Default.CaptureAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (shot is null) return null;

            await using var stream = await shot.OpenReadAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            var bytes = buffer.ToArray();
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Kilo(int bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : $"{bytes / 1024.0:0} KB";
}
