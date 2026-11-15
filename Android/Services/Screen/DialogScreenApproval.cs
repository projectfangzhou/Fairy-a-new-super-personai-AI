namespace FairyAI_Android.Services.Screen;

/// <summary>
/// The approval prompt for one screen action. It fails closed: no visible page means no approval, and
/// no approval means nothing happens.
/// </summary>
public sealed class DialogScreenApproval : IScreenApproval
{
    public async Task<bool> AskAsync(
        string capabilityLabel,
        string description,
        CancellationToken cancellationToken = default)
    {
        var page = Shell.Current?.CurrentPage;
        if (page is null) return false;

        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlertAsync(
                "允许这次操作？",
                $"{capabilityLabel}：{description}\n\n这会直接作用在当前屏幕上。",
                "允许",
                "拒绝"));
        }
        catch
        {
            return false;
        }
    }
}
