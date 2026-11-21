namespace FairyAI_Android.Pages;

public partial class ChatPage : ContentPage
{
    public ChatPage(ViewModels.MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        Controls.GlassTabBar.AttachTo(this);
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//settings");
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not ViewModels.MainViewModel vm) return;

        if (vm.Messages.Count == 0)
        {
            await DisplayAlertAsync("导出对话", "现在还没有对话可以导出。", "好");
            return;
        }

        var path = await new Services.ExportService()
            .ExportToMarkdownAsync(vm.Messages.ToList(), "仙灵对话");

        if (!File.Exists(path))
        {
            await DisplayAlertAsync("导出对话", $"导出没有成功：{path}", "好");
            return;
        }

        try
        {
            // Hands the file to whatever the user picks as a share target; the app never keeps a copy
            // of it outside its own cache.
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "导出对话",
                File = new ShareFile(path),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导出对话", $"已经写到 {path}，但打开分享面板失败了：{ex.Message}", "好");
        }
    }
}
