using FairyAI_Android.Services;
using FairyAI_Android.Services.Notes;
using FairyAI_Android.Services.Reminders;

namespace FairyAI_Android.Pages;

/// <summary>One row in the reminders list.</summary>
public sealed record ReminderRow(string Id, string Title, string When);

/// <summary>One row in the notes list.</summary>
public sealed record NoteRow(string Id, string Title, string Meta, string Snippet);

/// <summary>
/// Notes and reminders on the phone.
///
/// Both halves talk to the same stores the assistant's tools do, so a note the model saved shows up
/// here without a sync step, and a reminder the user cancels here is really gone. Reminders are
/// listed first: they are the half with a deadline.
/// </summary>
public partial class NotesPage : ContentPage
{
    private readonly NoteService _notes;
    private readonly ReminderService _reminders;
    private bool _remindersSubscribed;
    private bool _seeded;

    public NotesPage(NoteService notes, ReminderService reminders)
    {
        InitializeComponent();

        _notes = notes;
        _reminders = reminders;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_remindersSubscribed)
        {
            _reminders.Changed += OnRemindersChanged;
            _remindersSubscribed = true;
        }

        FillRepeatPicker();
        SeedDueTime();
        RefreshReminders();
        RefreshNotes(null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (!_remindersSubscribed) return;

        _reminders.Changed -= OnRemindersChanged;
        _remindersSubscribed = false;
    }

    private void OnRemindersChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshReminders);

    private void FillRepeatPicker()
    {
        if (ReminderRepeatPicker.Items.Count > 0) return;

        // MAUI cannot declare Picker items in XAML; the order matches ReminderRepeatParser below.
        ReminderRepeatPicker.Items.Add("一次");
        ReminderRepeatPicker.Items.Add("每天");
        ReminderRepeatPicker.Items.Add("每周");
        ReminderRepeatPicker.Items.Add("每月");
        ReminderRepeatPicker.Items.Add("每两周");
        ReminderRepeatPicker.SelectedIndex = 0;
    }

    private void SeedDueTime()
    {
        if (_seeded) return;

        var soon = DateTime.Now.AddHours(1);
        ReminderDatePicker.Date = soon.Date;
        ReminderTimePicker.Time = soon.TimeOfDay;
        _seeded = true;
    }

    private void RefreshReminders()
    {
        var failure = _reminders.LastError;

        ReminderStatusLabel.Text = string.IsNullOrWhiteSpace(failure)
            ? _reminders.Describe()
            : $"{_reminders.Describe()} · 上次出错：{failure}";

        var rows = _reminders.All()
            .Where(item => item.Enabled)
            .Select(item => new ReminderRow(
                item.Id,
                item.Title,
                ReminderSchedule.DescribeWhen(item.DueAt) + " · " + ReminderSchedule.DescribeRepeat(item.Repeat)))
            .ToList();

        ReminderList.ItemsSource = rows;
    }

    private void RefreshNotes(string? query)
    {
        NoteStatusLabel.Text = _notes.LoadError is { } loadError
            ? $"笔记文件读不出来：{loadError}（已保留原文件）"
            : $"共 {_notes.Count} 条笔记";

        var rows = new List<NoteRow>();

        if (string.IsNullOrWhiteSpace(query))
        {
            rows.AddRange(_notes.All().Select(note => new NoteRow(
                note.Id,
                note.Title,
                note.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                Preview(note.Body))));
        }
        else
        {
            foreach (var hit in _notes.Search(query, 20))
            {
                rows.Add(new NoteRow(
                    hit.Note.Id,
                    hit.Note.Title,
                    hit.Note.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                    hit.Snippet));
            }

            if (rows.Count == 0)
                NoteStatusLabel.Text = $"没有找到包含「{query}」的笔记";
        }

        NoteList.ItemsSource = rows;
    }

    private static string Preview(string body)
    {
        var flat = (body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 60 ? flat[..60] + "…" : flat;
    }

    private async void OnAddReminderClicked(object? sender, EventArgs e)
    {
        var title = ReminderTitleEntry.Text?.Trim() ?? "";
        if (title.Length == 0)
        {
            await DisplayAlertAsync("提醒", "先写一句话，提醒才有内容。", "知道了");
            return;
        }

        var due = (ReminderDatePicker.Date ?? DateTime.Today).Date + (ReminderTimePicker.Time ?? TimeSpan.Zero);
        if (due <= DateTime.Now.AddMinutes(1))
        {
            await DisplayAlertAsync("提醒", "这个时间已经过去了，换一个将来的时间。", "知道了");
            return;
        }

        var repeat = ReminderRepeatParser(ReminderRepeatPicker.SelectedIndex);
        var (ok, message, _) = _reminders.Add(new ReminderDraft(title, due, repeat));

        if (!ok)
        {
            await DisplayAlertAsync("提醒", message, "知道了");
            return;
        }

        ReminderTitleEntry.Text = "";
        RefreshReminders();
    }

    private static ReminderRepeat ReminderRepeatParser(int index) => index switch
    {
        1 => ReminderRepeat.Daily,
        2 => ReminderRepeat.Weekly,
        3 => ReminderRepeat.Monthly,
        4 => ReminderRepeat.EveryTwoWeeks,
        _ => ReminderRepeat.None,
    };

    private void OnCancelReminderClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;

        _reminders.Cancel(id);
        RefreshReminders();
    }

    private async void OnSaveNoteClicked(object? sender, EventArgs e)
    {
        var title = NoteTitleEntry.Text?.Trim() ?? "";
        var body = NoteBodyEditor.Text?.Trim() ?? "";

        if (title.Length == 0 && body.Length == 0)
        {
            await DisplayAlertAsync("笔记", "标题和正文都是空的，没有可保存的内容。", "知道了");
            return;
        }

        var result = _notes.Save(title, body);
        if (!result.Ok)
        {
            await DisplayAlertAsync("笔记", result.Message, "知道了");
            return;
        }

        NoteTitleEntry.Text = "";
        NoteBodyEditor.Text = "";
        NoteSearchEntry.Text = "";
        RefreshNotes(null);
    }

    private void OnSearchNotesClicked(object? sender, EventArgs e) =>
        RefreshNotes(NoteSearchEntry.Text);

    private void OnShowAllNotesClicked(object? sender, EventArgs e)
    {
        NoteSearchEntry.Text = "";
        RefreshNotes(null);
    }

    private void OnDeleteNoteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id }) return;

        _notes.Delete(id);
        RefreshNotes(NoteSearchEntry.Text);
    }
}
