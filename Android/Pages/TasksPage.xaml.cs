using FairyAI_Android.Services;
using FairyAI_Android.Services.Theme;

namespace FairyAI_Android.Pages;

/// <summary>
/// Mobile entry point for "what is waiting for me": the paired PC's tasks and its pending approvals.
/// Deliberately a page of its own rather than a copy of the desktop sidebar — the phone's job is one
/// screen, one decision at a time.
/// </summary>
public partial class TasksPage : ContentPage
{
    private readonly PairedDeskService _desk;

    public TasksPage(PairedDeskService desk)
    {
        InitializeComponent();
        _desk = desk;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!_desk.IsConfigured)
        {
            StatusLabel.Text = "还没有和电脑配对：到设置里配对一次，电脑才会下发令牌。";
            StatusLabel.TextColor = Glass.ReadColor("Theme.Error");
            PendingList.Clear();
            TaskList.Clear();
            PendingEmpty.IsVisible = true;
            TasksEmpty.IsVisible = true;
            return;
        }

        StatusLabel.Text = "正在读取…";
        StatusLabel.TextColor = Glass.ReadColor("Theme.TextSecondary");

        var (ok, message, desk) = await _desk.FetchAsync();
        StatusLabel.Text = message;
        StatusLabel.TextColor = ok ? Glass.ReadColor("Theme.Success") : Glass.ReadColor("Theme.Error");

        PendingList.Clear();
        TaskList.Clear();

        if (desk is null)
        {
            PendingEmpty.IsVisible = true;
            TasksEmpty.IsVisible = true;
            return;
        }

        foreach (var pending in desk.Pending) PendingList.Add(BuildPendingCard(pending));
        PendingEmpty.IsVisible = desk.Pending.Count == 0;

        foreach (var task in desk.Tasks) TaskList.Add(BuildTaskRow(task));
        TasksEmpty.IsVisible = desk.Tasks.Count == 0;
    }

    private View BuildPendingCard(PairedDeskPending pending)
    {
        var isQuestion = pending.Kind == "Question";

        var layout = new VerticalStackLayout { Spacing = 8 };

        layout.Add(new Label
        {
            Text = pending.Prompt,
            TextColor = Glass.ReadColor("Theme.TextPrimary"),
            FontSize = 13,
        });

        if (!string.IsNullOrWhiteSpace(pending.Risk))
        {
            layout.Add(new Label
            {
                Text = "风险：" + pending.Risk,
                TextColor = Glass.ReadColor("Theme.Error"),
                FontSize = 11,
            });
        }

        if (isQuestion)
        {
            var answer = new Entry
            {
                Placeholder = "在这里回答",
                BackgroundColor = Glass.ReadColor("Theme.SurfaceSunken"),
                TextColor = Glass.ReadColor("Theme.TextPrimary"),
            };
            var send = new Button
            {
                Text = "提交回答",
                BackgroundColor = Glass.ReadColor("Theme.Accent"),
                TextColor = Glass.ReadColor("Theme.TextOnAccent"),
                CornerRadius = 10,
                HeightRequest = 40,
            };
            send.Clicked += async (_, _) => await AnswerTextAsync(pending.InteractionId, answer.Text, send);
            layout.Add(answer);
            layout.Add(send);
        }
        else
        {
            var row = new HorizontalStackLayout { Spacing = 10 };

            var allow = new Button
            {
                Text = "允许",
                BackgroundColor = Glass.ReadColor("Theme.Success"),
                TextColor = Glass.ReadColor("Theme.TextOnAccent"),
                CornerRadius = 10,
                HeightRequest = 40,
            };
            allow.Clicked += async (_, _) => await AnswerAsync(pending.InteractionId, true, allow);

            var deny = new Button
            {
                Text = "拒绝",
                BackgroundColor = Glass.ReadColor("Theme.BorderStrong"),
                TextColor = Glass.ReadColor("Theme.TextPrimary"),
                CornerRadius = 10,
                HeightRequest = 40,
            };
            deny.Clicked += async (_, _) => await AnswerAsync(pending.InteractionId, false, deny);

            row.Add(allow);
            row.Add(deny);
            layout.Add(row);
        }

        return new Border
        {
            BackgroundColor = Glass.ReadColor("Theme.SurfaceCard"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Padding = 16,
            Content = layout,
        };
    }

    private static View BuildTaskRow(PairedDeskTask task)
    {
        var layout = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };

        var title = new VerticalStackLayout { Spacing = 2 };
        title.Add(new Label { Text = task.Title, TextColor = Glass.ReadColor("Theme.TextPrimary"), FontSize = 13 });
        title.Add(new Label { Text = task.TaskId, TextColor = Glass.ReadColor("Theme.TextMuted"), FontSize = 11 });

        var state = new Label
        {
            Text = task.State,
            TextColor = Glass.ReadColor("Theme.Accent"),
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
        };

        layout.Add(title, 0);
        layout.Add(state, 1);

        return new Border
        {
            BackgroundColor = Glass.ReadColor("Theme.SurfaceTile"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = 12,
            Content = layout,
        };
    }

    private async Task AnswerAsync(string interactionId, bool approved, Button button)
    {
        button.IsEnabled = false;
        var (ok, message) = await _desk.AnswerAsync(interactionId, approved);
        StatusLabel.Text = message;
        StatusLabel.TextColor = ok ? Glass.ReadColor("Theme.Success") : Glass.ReadColor("Theme.Error");

        if (ok) await RefreshAsync();
        else button.IsEnabled = true;
    }

    private async Task AnswerTextAsync(string interactionId, string? text, Button button)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusLabel.Text = "回答不能为空";
            StatusLabel.TextColor = Glass.ReadColor("Theme.Error");
            return;
        }

        button.IsEnabled = false;
        var (ok, message) = await _desk.AnswerTextAsync(interactionId, text.Trim());
        StatusLabel.Text = message;
        StatusLabel.TextColor = ok ? Glass.ReadColor("Theme.Success") : Glass.ReadColor("Theme.Error");

        if (ok) await RefreshAsync();
        else button.IsEnabled = true;
    }
}
