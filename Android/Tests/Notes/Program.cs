using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using FairyAI_Android.Services;
using FairyAI_Android.Services.Knowledge;
using FairyAI_Android.Services.Notes;
using FairyAI_Android.Services.Reminders;
using FairyAI_Android.Services.Tools;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

// The sandbox lives next to the test binary: the machine's TEMP points at a directory this account
// cannot write, so nothing here may rely on Path.GetTempPath().
var sandbox = Path.Combine(AppContext.BaseDirectory, "sandbox");
if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
Directory.CreateDirectory(sandbox);

string PathFor(string name) => Path.Combine(sandbox, name);

var fixedNow = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Unspecified);

// ------------------------------------------------------------------ notes: storage

var notesFile = PathFor("notes.json");
var notes = new NoteStore(notesFile, () => new DateTimeOffset(fixedNow, TimeSpan.FromHours(8)));

Assert(notes.Count == 0, "a new store is empty");
Assert(notes.LoadError is null, "a missing file is not an error");

Assert(NoteStore.NormalizeTags(null).Count == 0, "no tags is fine");
var normalized = NoteStore.NormalizeTags(new[] { " 工作 ", "工作", "生活", "", "  ", "很长很长很长很长很长很长很长很长很长很长很长很长很长" });
Assert(normalized.Count == 3, "tags are trimmed and deduplicated");
Assert(normalized[0] == "工作", "tag text is kept");
Assert(normalized[2].Length <= NoteStore.MaxTagLength, "an over-long tag is cut, not rejected");

var refused = notes.Save(new NoteDraft("   ", "   "));
Assert(!refused.Ok && notes.Count == 0, "an empty note is refused and not stored");

var created = notes.Save(new NoteDraft("周会要点", "下周一上午十点开会，讨论三件事：预算、排期、招聘。"));
Assert(created.Ok && created.Note is not null, "a note with content is saved");
Assert(created.Note!.Id.StartsWith("n", StringComparison.Ordinal), "a new note gets an id");
Assert(created.Note.CreatedAt.UtcDateTime == fixedNow.AddHours(-8), "the store's clock sets the timestamp");

var derived = notes.Save(new NoteDraft("", "第一行就是标题\n第二行是正文"));
Assert(derived.Ok && derived.Note!.Title == "第一行就是标题", "an empty title is taken from the first line");

var tooLong = notes.Save(new NoteDraft(new string('长', NoteStore.MaxTitleLength + 1), "正文"));
Assert(!tooLong.Ok, "an over-long title is refused");

var updated = notes.Save(new NoteDraft("周会要点（改了）", "改成下周二。", new[] { "工作" }) { Id = created.Note.Id });
Assert(updated.Ok && updated.Note!.Title == "周会要点（改了）", "an existing note can be updated by id");
Assert(updated.Note!.CreatedAt == created.Note!.CreatedAt, "updating keeps the original creation time");
Assert(notes.Count == 2, "updating does not create a second note");

var ghost = notes.Save(new NoteDraft("不存在的笔记", "x") { Id = "ndoesnotexist" });
Assert(!ghost.Ok && notes.Count == 2, "an unknown id is refused instead of creating a note");

Assert(notes.SetPinned(derived.Note!.Id, true), "a note can be pinned");
Assert(notes.All()[0].Id == derived.Note.Id, "the pinned note sorts first");

var reopened = new NoteStore(notesFile, () => new DateTimeOffset(fixedNow, TimeSpan.FromHours(8)));
Assert(reopened.Count == 2, "notes survive a restart");
Assert(reopened.All().Any(note => note.Title == "周会要点（改了）"), "the edited title is on disk");

Assert(reopened.Delete(created.Note.Id), "a note can be deleted");
Assert(!reopened.Delete(created.Note.Id), "deleting twice reports nothing to delete");
Assert(new NoteStore(notesFile).Count == 1, "the deletion reached the file");

// ------------------------------------------------------------------ notes: search

var searchNotes = new NoteStore(PathFor("search.json"), () => DateTimeOffset.UnixEpoch);
searchNotes.Save(new NoteDraft("会议纪要", "预算和排期都通过了。"));
searchNotes.Save(new NoteDraft("购物清单", "牛奶、鸡蛋。会议之后去买。"));
searchNotes.Save(new NoteDraft("随手记", "今天的天气不错。"));

var titleHit = searchNotes.Search("会议");
Assert(titleHit.Count == 2, "a query matches title and body, not just titles");
Assert(titleHit[0].Note.Title == "会议纪要", "a title match outranks a body match");
Assert(titleHit[1].Snippet.Contains("会议", StringComparison.Ordinal), "the snippet shows where the match is");

Assert(searchNotes.Search("会议 预算").Count == 1, "every query term must match the same note");
Assert(searchNotes.Search("会议 不存在").Count == 0, "a term that matches nothing removes the note");
Assert(searchNotes.Search("").Count == 3, "an empty query lists everything");
Assert(searchNotes.Search("会议", 1).Count == 1, "the limit is respected");
Assert(searchNotes.Search("天气")[0].Note.Title == "随手记", "body-only match still finds the note");

var longBody = new NoteStore(PathFor("long.json"), () => DateTimeOffset.UnixEpoch);
longBody.Save(new NoteDraft("长文", new string('填', 300) + "关键点在中间" + new string('充', 300)));
var longHit = longBody.Search("关键点");
Assert(longHit.Count == 1, "a match deep inside a long note is found");
Assert(longHit[0].Snippet.Length < 200, "the snippet is clipped instead of returning the whole body");
Assert(longHit[0].Snippet.StartsWith("…", StringComparison.Ordinal), "a clipped snippet says it was clipped");

// A damaged file must be kept aside and reported, never silently treated as "no notes".
var damagedFile = PathFor("damaged.json");
File.WriteAllText(damagedFile, "{ this is not json");
var damaged = new NoteStore(damagedFile);
Assert(damaged.LoadError is not null, "a damaged notes file is reported");
Assert(damaged.Count == 0, "a damaged file leaves an empty store");
Assert(!File.Exists(damagedFile), "the damaged file is moved aside, not deleted");
Assert(Directory.GetFiles(sandbox, "damaged.json.damaged-*").Length == 1, "the damaged bytes are kept");

// ------------------------------------------------------------------ reminders: recurrence

var daily = new Reminder { Id = "r1", Title = "吃药", DueAt = new DateTime(2026, 11, 14, 9, 0, 0), Repeat = ReminderRepeat.Daily };
Assert(ReminderSchedule.NextAfter(daily, new DateTime(2026, 11, 14, 8, 0, 0)) == new DateTime(2026, 11, 14, 9, 0, 0),
    "a daily reminder keeps today's time if it is still ahead");
Assert(ReminderSchedule.NextAfter(daily, new DateTime(2026, 11, 14, 9, 0, 0)) == new DateTime(2026, 11, 15, 9, 0, 0),
    "the next occurrence is strictly after now");
Assert(ReminderSchedule.NextAfter(daily, new DateTime(2026, 11, 20, 12, 0, 0)) == new DateTime(2026, 11, 21, 9, 0, 0),
    "a daily reminder stays on its original time of day");

var oneShot = new Reminder { Id = "r2", Title = "开会", DueAt = new DateTime(2026, 11, 14, 15, 0, 0) };
Assert(ReminderSchedule.NextAfter(oneShot, new DateTime(2026, 11, 14, 14, 0, 0)) == new DateTime(2026, 11, 14, 15, 0, 0),
    "a one-shot reminder in the future has a next occurrence");
Assert(ReminderSchedule.NextAfter(oneShot, new DateTime(2026, 11, 14, 16, 0, 0)) is null,
    "a one-shot reminder in the past has none");

var weekly = new Reminder { Id = "r3", Title = "周报", DueAt = new DateTime(2026, 11, 14, 18, 0, 0), Repeat = ReminderRepeat.Weekly };
Assert(ReminderSchedule.NextAfter(weekly, new DateTime(2026, 11, 15, 0, 0, 0)) == new DateTime(2026, 11, 21, 18, 0, 0),
    "a weekly reminder moves by seven days and keeps the weekday");

Assert(ReminderSchedule.ParseRepeat("每两周") == ReminderRepeat.EveryTwoWeeks, "biweekly parses in Chinese");
Assert(ReminderSchedule.ParseRepeat("biweekly") == ReminderRepeat.EveryTwoWeeks, "biweekly parses in English");

var biweekly = new Reminder
{
    Id = "r8",
    Title = "双周会",
    DueAt = new DateTime(2026, 11, 14, 10, 0, 0),
    Repeat = ReminderRepeat.EveryTwoWeeks,
};
Assert(ReminderSchedule.NextAfter(biweekly, new DateTime(2026, 11, 14, 11, 0, 0)) == new DateTime(2026, 11, 28, 10, 0, 0),
    "every two weeks skips a week");
Assert(ReminderSchedule.NextAfter(biweekly, new DateTime(2026, 11, 29, 0, 0, 0)) == new DateTime(2026, 12, 12, 10, 0, 0),
    "every two weeks keeps stepping from its own schedule");
Assert(ReminderSchedule.DescribeRepeat(ReminderRepeat.EveryTwoWeeks) == "每两周", "the biweekly rule has its own label");

var monthlyEnd = new Reminder { Id = "r4", Title = "房租", DueAt = new DateTime(2027, 1, 31, 10, 0, 0), Repeat = ReminderRepeat.Monthly };

Assert(ReminderSchedule.NextAfter(monthlyEnd, new DateTime(2027, 2, 1, 0, 0, 0)) == new DateTime(2027, 2, 28, 10, 0, 0),
    "monthly clamps to the last day of a short month");
Assert(ReminderSchedule.NextAfter(monthlyEnd, new DateTime(2027, 3, 1, 0, 0, 0)) == new DateTime(2027, 3, 31, 10, 0, 0),
    "monthly counts from the original day, so it does not drift after February");

Assert(ReminderSchedule.IsDue(daily, new DateTime(2026, 11, 14, 8, 59, 0)) == false, "a reminder is not due early");
Assert(ReminderSchedule.IsDue(daily, new DateTime(2026, 11, 14, 9, 0, 0)), "a reminder is due at its time");
daily.Enabled = false;
Assert(ReminderSchedule.IsDue(daily, new DateTime(2026, 11, 14, 10, 0, 0)) == false, "a disabled reminder is never due");
daily.Enabled = true;
daily.LastFiredAt = new DateTime(2026, 11, 14, 9, 0, 5);
Assert(ReminderSchedule.IsDue(daily, new DateTime(2026, 11, 14, 9, 30, 0)) == false,
    "an occurrence that already fired is not due again");

var backlog = new Reminder { Id = "r5", Title = "喝水", DueAt = new DateTime(2026, 11, 11, 9, 0, 0), Repeat = ReminderRepeat.Daily };
Assert(ReminderSchedule.MissedOccurrences(backlog, new DateTime(2026, 11, 14, 10, 0, 0)) == 4,
    "four daily occurrences have piled up");
Assert(ReminderSchedule.MissedOccurrences(backlog, new DateTime(2026, 11, 10, 10, 0, 0)) == 0,
    "nothing is missed before the first occurrence");

var advancedOnce = new Reminder { Id = "r6", Title = "一次", DueAt = new DateTime(2026, 11, 14, 9, 0, 0) };
Assert(!ReminderSchedule.Advance(advancedOnce, new DateTime(2026, 11, 14, 9, 0, 0)), "a fired one-shot turns itself off");
Assert(advancedOnce.FireCount == 1 && advancedOnce.LastFiredAt == new DateTime(2026, 11, 14, 9, 0, 0),
    "firing is recorded");

var advancedDaily = new Reminder { Id = "r7", Title = "每天", DueAt = new DateTime(2026, 11, 11, 9, 0, 0), Repeat = ReminderRepeat.Daily };
Assert(ReminderSchedule.Advance(advancedDaily, new DateTime(2026, 11, 14, 10, 0, 0)), "a repeating reminder stays enabled");
Assert(advancedDaily.DueAt == new DateTime(2026, 11, 15, 9, 0, 0),
    "after a long absence the reminder jumps to the next future time, not the backlog behind it");

// ------------------------------------------------------------------ reminders: parsing

Assert(ReminderSchedule.ParseDue("+30m", fixedNow) == fixedNow.AddMinutes(30), "+30m is thirty minutes");
Assert(ReminderSchedule.ParseDue("+2h", fixedNow) == fixedNow.AddHours(2), "+2h is two hours");
Assert(ReminderSchedule.ParseDue("+1d", fixedNow) == fixedNow.AddDays(1), "+1d is a day");
Assert(ReminderSchedule.ParseDue("+45", fixedNow) == fixedNow.AddMinutes(45), "a bare offset reads as minutes");
Assert(ReminderSchedule.ParseDue("2026-12-01 09:30", fixedNow) == new DateTime(2026, 12, 1, 9, 30, 0),
    "an absolute time is taken as given");
Assert(ReminderSchedule.ParseDue("09:30", fixedNow) == new DateTime(2026, 11, 14, 9, 30, 0),
    "a bare time later today stays today");
Assert(ReminderSchedule.ParseDue("07:30", fixedNow) == new DateTime(2026, 11, 15, 7, 30, 0),
    "a bare time that has passed rolls to tomorrow");
Assert(ReminderSchedule.ParseDue("大概下午吧", fixedNow) is null, "unparseable text is refused, not guessed");
Assert(ReminderSchedule.ParseDue("+0m", fixedNow) is null, "a zero offset is refused");
Assert(ReminderSchedule.ParseDue("", fixedNow) is null, "an empty time is refused");

Assert(ReminderSchedule.ParseRepeat("daily") == ReminderRepeat.Daily, "repeats parse in English");
Assert(ReminderSchedule.ParseRepeat("每天") == ReminderRepeat.Daily, "repeats parse in Chinese");
Assert(ReminderSchedule.ParseRepeat(null) == ReminderRepeat.None, "no repeat means once");
Assert(ReminderSchedule.ParseRepeat("每三周") is null, "an unknown repeat is refused");

Assert(ReminderSchedule.DescribeWhen(new DateTime(2026, 11, 14, 15, 0, 0), fixedNow) == "今天 15:00",
    "today is described as today");
Assert(ReminderSchedule.DescribeWhen(new DateTime(2026, 11, 15, 8, 5, 0), fixedNow) == "明天 08:05",
    "tomorrow is described as tomorrow");
Assert(ReminderSchedule.DescribeRepeat(ReminderRepeat.Monthly) == "每月", "repeats have Chinese names");

// ------------------------------------------------------------------ reminders: store

var reminderFile = PathFor("reminders.json");
var reminders = new ReminderStore(reminderFile, () => fixedNow);

var past = reminders.Add(new ReminderDraft("过去了", fixedNow.AddMinutes(-1)));
Assert(!past.Ok && reminders.Count == 0, "a time in the past is refused rather than fired immediately");

var emptyReminder = reminders.Add(new ReminderDraft("  ", fixedNow.AddHours(1)) { Note = "   " });
Assert(!emptyReminder.Ok, "a reminder without content is refused");

var added = reminders.Add(new ReminderDraft("开会", fixedNow.AddHours(1), ReminderRepeat.Daily));
Assert(added.Ok && added.Reminder!.Id.StartsWith("r", StringComparison.Ordinal), "a future reminder is stored with an id");
Assert(reminders.Add(new ReminderDraft("太远", fixedNow.AddYears(60))).Ok == false, "an absurdly distant time is refused");

var reopenedReminders = new ReminderStore(reminderFile, () => fixedNow);
Assert(reopenedReminders.Count == 1, "reminders survive a restart");
Assert(reopenedReminders.All()[0].Repeat == ReminderRepeat.Daily, "the repeat rule is on disk");
Assert(reopenedReminders.Describe(fixedNow).Contains("待提醒 1 条", StringComparison.Ordinal), "the summary counts what is pending");

var dueNow = new ReminderStore(PathFor("due.json"), () => fixedNow);
dueNow.Add(new ReminderDraft("早", fixedNow.AddMinutes(5)));
dueNow.Add(new ReminderDraft("中", fixedNow.AddMinutes(10)));
dueNow.Add(new ReminderDraft("晚", fixedNow.AddMinutes(15)));
Assert(dueNow.Due(fixedNow, 10).Count == 0, "nothing is due before its time");
var later = fixedNow.AddMinutes(30);
Assert(dueNow.Due(later, 10).Count == 3, "all three are due after their time");
Assert(dueNow.Due(later, 2).Count == 2, "the due list is capped");
Assert(dueNow.Due(later, 10)[0].Title == "早", "the soonest reminder comes first");

var marked = dueNow.MarkFired(dueNow.Due(later, 1)[0].Id, later);
Assert(marked is not null && marked.Enabled == false, "marking a one-shot reminder as fired disables it");
Assert(dueNow.Due(later, 10).Count == 2, "a fired reminder is no longer due");
Assert(new ReminderStore(PathFor("due.json"), () => later).Due(later, 10).Count == 2, "the fired state reached the file");

var legacy = PathFor("legacy.json");
File.WriteAllText(legacy, "[{\"id\":\"abc\",\"time\":\"2026-11-14T09:00:00\",\"message\":\"旧格式\",\"triggered\":false}]");
var migrated = new ReminderStore(legacy, () => fixedNow);
Assert(migrated.SkippedInvalid == 1 && migrated.Count == 0, "a record that is not a reminder is dropped instead of firing");

var cancelling = new ReminderStore(PathFor("cancel.json"), () => fixedNow);
var toCancel = cancelling.Add(new ReminderDraft("取消我", fixedNow.AddHours(2)));
Assert(cancelling.Cancel(toCancel.Reminder!.Id), "a reminder can be cancelled");
Assert(!cancelling.Cancel(toCancel.Reminder.Id), "cancelling twice reports nothing to cancel");
Assert(new ReminderStore(PathFor("cancel.json"), () => fixedNow).Count == 0, "the cancellation reached the file");

// ------------------------------------------------------------------ reminders: firing

var recordsSink = new RecordingSink();
var fireStore = new ReminderStore(PathFor("fire.json"), () => fixedNow);
var alarm = new RecordingAlarm();
var service = new ReminderService(recordsSink, fireStore, alarm, () => fixedNow) { Interval = TimeSpan.FromHours(1) };

service.Add(new ReminderDraft("喝水", fixedNow.AddMinutes(1)));
Assert(alarm.Scheduled.Count == 1, "adding a reminder schedules a platform alarm");

var notYet = await service.CheckAsync();
Assert(notYet == 0 && recordsSink.Sent.Count == 0, "nothing is delivered before the time");

var now = fixedNow.AddMinutes(2);
var lateService = new ReminderService(recordsSink, fireStore, alarm, () => now) { Interval = TimeSpan.FromHours(1) };
var delivered = await lateService.CheckAsync();
Assert(delivered == 1 && recordsSink.Sent.Count == 1, "a due reminder is delivered");
Assert(recordsSink.Sent[0].Channel == NotificationChannels.Reminder, "reminders go to their own channel");
Assert(recordsSink.Sent[0].Title == "喝水", "the notification carries the reminder title");
Assert(recordsSink.Sent[0].Important, "a reminder is marked important");
Assert(fireStore.All()[0].Enabled == false, "the one-shot reminder is switched off after delivery");
Assert(await lateService.CheckAsync() == 0, "checking again does not deliver it twice");
Assert(lateService.Delivered == 1, "the delivered count is honest");

var refusedSink = new RecordingSink { Accept = false };
var refusedStore = new ReminderStore(PathFor("refused.json"), () => fixedNow);
refusedStore.Add(new ReminderDraft("送不出去", fixedNow.AddMinutes(1)));
var refusedService = new ReminderService(refusedSink, refusedStore, null, () => now) { Interval = TimeSpan.FromHours(1) };
Assert(await refusedService.CheckAsync() == 0, "a refused notification is not counted as delivered");
Assert(refusedService.LastError is not null, "a refused notification says why");
var stillPending = refusedStore.All()[0];
Assert(stillPending.Enabled && stillPending.LastFiredAt is null, "an undelivered reminder stays armed");

var noSinkService = new ReminderService(new NullNotificationSink(), refusedStore, null, () => now);
Assert(await noSinkService.CheckAsync() == 0, "a platform without notifications delivers nothing");
Assert(noSinkService.LastError is not null, "a platform without notifications says so");

var repeatStore = new ReminderStore(PathFor("repeat.json"), () => fixedNow);
var repeatSink = new RecordingSink();
var repeatAlarm = new RecordingAlarm();
var repeatService = new ReminderService(repeatSink, repeatStore, repeatAlarm, () => fixedNow) { Interval = TimeSpan.FromHours(1) };
Assert(repeatService.Add(new ReminderDraft("每天打卡", fixedNow.AddMinutes(1), ReminderRepeat.Daily)).Ok,
    "a future repeating reminder is accepted");

var afterFire = fixedNow.AddMinutes(30);
var firedService = new ReminderService(repeatSink, repeatStore, repeatAlarm, () => afterFire) { Interval = TimeSpan.FromHours(1) };
Assert(await firedService.CheckAsync() == 1, "a due repeating reminder is delivered once");
var repeating = repeatStore.All()[0];
Assert(repeating.Enabled, "a repeating reminder stays enabled after firing");
Assert(repeating.DueAt == fixedNow.AddDays(1).AddMinutes(1), "it moves to tomorrow's occurrence, not the backlog");
Assert(repeating.LastFiredAt == afterFire, "the firing time is recorded");
Assert(repeatAlarm.Scheduled.Count == 2, "firing reschedules the platform alarm for the next occurrence");
Assert(alarm.Cancelled.Count == 1, "a finished one-shot reminder has its platform alarm cancelled");

var overdueStore = new ReminderStore(PathFor("overdue.json"), () => fixedNow);
var overdueSink = new RecordingSink();
var overdueService = new ReminderService(overdueSink, overdueStore, null, () => fixedNow) { Interval = TimeSpan.FromHours(1) };
overdueService.Add(new ReminderDraft("吃药", fixedNow.AddMinutes(1), ReminderRepeat.Daily));
var threeDaysLater = fixedNow.AddDays(3).AddMinutes(5);
var overdueCheck = new ReminderService(overdueSink, overdueStore, null, () => threeDaysLater);
Assert(await overdueCheck.CheckAsync() == 1, "three days of missed occurrences produce one notification");
Assert(overdueSink.Sent[0].Body.Contains("错过", StringComparison.Ordinal), "the notification says how many were missed");
Assert(overdueSink.Sent.Count == 1, "the backlog is not replayed one notification per day");

var floodStore = new ReminderStore(PathFor("flood.json"), () => fixedNow);
var floodSink = new RecordingSink();
var floodService = new ReminderService(floodSink, floodStore, null, () => fixedNow) { Interval = TimeSpan.FromHours(1) };
for (var index = 0; index < 5; index++)
    floodService.Add(new ReminderDraft("提醒 " + index, fixedNow.AddMinutes(1 + index)));

var muchLater = fixedNow.AddMinutes(10);
var floodCheck = new ReminderService(floodSink, floodStore, null, () => muchLater);
Assert(await floodCheck.CheckAsync() == ReminderService.MaxNotificationsPerCheck,
    "one check delivers at most the configured number of notifications");
Assert(new ReminderStore(PathFor("flood.json"), () => muchLater).Due(muchLater, 10).Count
       == 5 - ReminderService.MaxNotificationsPerCheck,
    "the rest stay due for the next check instead of being skipped");

var describeService = new ReminderService(new RecordingSink(), fireStore, null, () => now);
Assert(describeService.Describe().Contains("只在应用运行时提醒", StringComparison.Ordinal),
    "without a platform alarm the service says reminders only work while it runs");
Assert(new ReminderService(new RecordingSink(), repeatStore, repeatAlarm, () => now).Describe()
        .Contains("测试用闹钟", StringComparison.Ordinal),
    "with a platform alarm the service reports what the platform guarantees");

// ------------------------------------------------------------------ knowledge

var knowledge = KnowledgeIndex.Build(new[]
{
    new KnowledgeDocument("周会记录", "周一上午十点在三楼会议室开会。议题是预算、排期和招聘。\n\n会后每个人更新自己的排期表。"),
    new KnowledgeDocument("nutrition", "Milk and eggs are cheap protein. Eggs keep for weeks."),
});

Assert(knowledge.Documents.Count == 2, "both documents are indexed");
Assert(knowledge.PassageCount >= 2, "a document is split into passages");
Assert(knowledge.Search("预算").Count == 1, "a Chinese keyword finds its document");
Assert(knowledge.Search("预算")[0].Document == "周会记录", "the hit names the document it came from");
Assert(knowledge.Search("会议室")[0].Passage.Contains("会议室", StringComparison.Ordinal),
    "the passage contains what was searched for");
Assert(knowledge.Search("milk").Count == 1, "Latin search is case-insensitive");
Assert(knowledge.Search("MILK")[0].Document == "nutrition", "the Latin hit points at the right document");
Assert(knowledge.Search("量子计算").Count == 0, "a term nobody wrote returns nothing");
Assert(knowledge.Search("会议排期").Count == 1, "a multi-word query that is really in the text still matches");
Assert(knowledge.Search("").Count == 0, "an empty query returns nothing");
Assert(knowledge.Search("周会").Count >= 1, "a document name is searchable");

var manyPassages = KnowledgeIndex.Build(new[]
{
    new KnowledgeDocument("长篇", string.Join("\n\n", Enumerable.Range(0, 12).Select(i => "第 " + i + " 段：" + new string('文', 120)))),
});
Assert(manyPassages.PassageCount > 1, "a long document becomes several passages");
var longPassageHit = manyPassages.Search("第 7 段");
Assert(longPassageHit.Count == 1 && longPassageHit[0].Passage.Contains("第 7 段", StringComparison.Ordinal),
    "a match in a later passage is found and quoted");
Assert(longPassageHit[0].Passage.Length <= 200, "the quoted passage is clipped to a readable size");

var tokens = KnowledgeIndex.Tokenize("会议 room 2026");
Assert(tokens.Contains("会") && tokens.Contains("议"), "Chinese is tokenised per character");
Assert(tokens.Contains("room"), "Latin words become one token each");
Assert(tokens.Contains("2026"), "numbers are tokens too");

// ------------------------------------------------------------------ tools

var toolNotes = new NoteStore(PathFor("tool-notes.json"), () => DateTimeOffset.UnixEpoch);
var saveTool = new NoteSaveTool(toolNotes);
var saved = await saveTool.ExecuteAsync(new JsonObject { ["title"] = "买牛奶", ["body"] = "顺便买鸡蛋", ["tags"] = "生活, 采购" });
Assert(saved.Contains("编号 n", StringComparison.Ordinal), "note_save answers with the id the model needs next");
Assert(toolNotes.Count == 1 && toolNotes.All()[0].Tags.Count == 2, "note_save really stored the note and its tags");
Assert((await saveTool.ExecuteAsync(new JsonObject { ["title"] = "", ["body"] = "" })).Contains("没能保存", StringComparison.Ordinal),
    "note_save refuses an empty note instead of pretending");

var searchTool = new NoteSearchTool(toolNotes);
var found = await searchTool.ExecuteAsync(new JsonObject { ["query"] = "牛奶" });
Assert(found.Contains("买牛奶", StringComparison.Ordinal), "note_search finds the note");
Assert(found.Contains("[n", StringComparison.Ordinal), "the search result carries the id");
Assert((await searchTool.ExecuteAsync(new JsonObject { ["query"] = "跑车" })).Contains("没有找到", StringComparison.Ordinal),
    "a search that matches nothing says so");

var deleteTool = new NoteDeleteTool(toolNotes);
Assert((await deleteTool.ExecuteAsync(new JsonObject { ["id"] = "nnope" })).Contains("没有编号", StringComparison.Ordinal),
    "note_delete refuses an id that does not exist");
var realId = toolNotes.All()[0].Id;
Assert((await deleteTool.ExecuteAsync(new JsonObject { ["id"] = realId })).Contains("已删除", StringComparison.Ordinal),
    "note_delete removes a real note");
Assert(toolNotes.Count == 0, "the note is gone from the store");

var toolReminderStore = new ReminderStore(PathFor("tool-reminders.json"), () => fixedNow);
var toolReminderService = new ReminderService(new RecordingSink(), toolReminderStore, null, () => fixedNow);
var createTool = new ReminderCreateTool(toolReminderService, () => fixedNow);

var createdReminder = await createTool.ExecuteAsync(new JsonObject { ["title"] = "拿快递", ["due"] = "+30m" });
Assert(createdReminder.Contains("编号 r", StringComparison.Ordinal), "reminder_create reports the id it created");
Assert(toolReminderStore.Count == 1, "the reminder really exists");
Assert((await createTool.ExecuteAsync(new JsonObject { ["title"] = "x", ["due"] = "明天吧" })).Contains("看不懂的时间", StringComparison.Ordinal),
    "an unparseable time is reported, not guessed");
Assert((await createTool.ExecuteAsync(new JsonObject { ["title"] = "x", ["due"] = "+1h", ["repeat"] = "每三周" })).Contains("看不懂的重复方式", StringComparison.Ordinal),
    "an unknown repeat is reported");
Assert((await createTool.ExecuteAsync(new JsonObject { ["title"] = "", ["due"] = "+1h" })).Contains("没有给出提醒内容", StringComparison.Ordinal),
    "a reminder without content is refused before it reaches the store");

var listTool = new ReminderListTool(toolReminderService, () => fixedNow);
var listed = await listTool.ExecuteAsync(new JsonObject());
Assert(listed.Contains("拿快递", StringComparison.Ordinal), "reminder_list shows what is pending");
Assert(listed.Contains("[r", StringComparison.Ordinal), "reminder_list carries the ids");

var cancelTool = new ReminderCancelTool(toolReminderService);
var pendingId = toolReminderStore.All()[0].Id;
Assert((await cancelTool.ExecuteAsync(new JsonObject { ["id"] = pendingId })).Contains("已取消", StringComparison.Ordinal),
    "reminder_cancel cancels a real reminder");
Assert(toolReminderStore.Count == 0, "the reminder is gone from the store");
Assert((await cancelTool.ExecuteAsync(new JsonObject { ["id"] = pendingId })).Contains("没有编号", StringComparison.Ordinal),
    "cancelling something that no longer exists says so");

var emptyKnowledge = KnowledgeIndex.Build(Array.Empty<KnowledgeDocument>());
var emptySearchTool = new KnowledgeSearchTool(new FakeKnowledge(emptyKnowledge));
Assert((await emptySearchTool.ExecuteAsync(new JsonObject { ["query"] = "任何" })).Contains("还没有文档", StringComparison.Ordinal),
    "knowledge_search on an empty base says the base is empty");

var realSearchTool = new KnowledgeSearchTool(new FakeKnowledge(knowledge));
var knowledgeAnswer = await realSearchTool.ExecuteAsync(new JsonObject { ["query"] = "排期" });
Assert(knowledgeAnswer.Contains("周会记录", StringComparison.Ordinal), "knowledge_search cites the document");
Assert(knowledgeAnswer.Contains("排期", StringComparison.Ordinal), "knowledge_search returns the passage");
Assert((await realSearchTool.ExecuteAsync(new JsonObject { ["query"] = "量子计算" })).Contains("没有和", StringComparison.Ordinal),
    "a query that matches nothing says so");
var missingQuery = await realSearchTool.ExecuteAsync(new JsonObject());
Assert(missingQuery.Contains("没有给出查询词", StringComparison.Ordinal), "knowledge_search refuses an empty query");

// A tool's schema is what the model actually sees, so the new parameters have to reach it.
var schema = JsonSerializer.Serialize(ToolSchema.BuildDefinition(createTool));
using (var document = JsonDocument.Parse(schema))
{
    var function = document.RootElement.GetProperty("function");
    Assert(function.GetProperty("name").GetString() == "reminder_create", "the reminder tool is announced by name");
    var required = function.GetProperty("parameters").GetProperty("required");
    Assert(required.EnumerateArray().Any(item => item.GetString() == "due"), "the due parameter is marked required");
    Assert(function.GetProperty("parameters").GetProperty("properties").TryGetProperty("repeat", out _),
        "the optional repeat parameter is announced too");
}

// ------------------------------------------------------------------ note sync: the wire

var wireSource = new Note
{
    Id = "w1",
    Title = "标题",
    Body = "正文",
    Tags = new List<string> { "工作" },
    Pinned = true,
    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_000),
    UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(2_000),
    DeletedAt = DateTimeOffset.FromUnixTimeMilliseconds(3_000),
};

var wireNote = NoteSyncWire.ToWire(wireSource);
Assert(wireNote.DeletedAtMs == 3_000, "a tombstone keeps its stamp on the wire");
Assert(wireNote.Pinned, "the pinned flag travels");

var wireBack = NoteSyncWire.FromWire(wireNote);
Assert(wireBack.Id == "w1" && wireBack.Body == "正文" && wireBack.Pinned, "a wire note round-trips");
Assert(wireBack.DeletedAt?.ToUnixTimeMilliseconds() == 3_000, "the tombstone stamp survives the round trip");

var requestJson = NoteSyncWire.BuildRequestBody(1_234, new[] { wireSource }, "phone-9");
using (var document = JsonDocument.Parse(requestJson))
{
    var root = document.RootElement;
    Assert(root.GetProperty("since").GetInt64() == 1_234, "the request carries the watermark");
    Assert(root.GetProperty("device").GetString() == "phone-9", "the request carries the device name");

    var noteJson = root.GetProperty("notes")[0];
    Assert(noteJson.GetProperty("id").GetString() == "w1", "the note id is on the wire");
    Assert(noteJson.GetProperty("body").GetString() == "正文", "the body travels under 'body'");
    Assert(noteJson.GetProperty("deletedAtMs").GetInt64() == 3_000, "deletions travel as stamps, not as a second list");
}

const string syncReply = """
{"success":true,"serverTime":7777,"notes":[
  {"id":"s1","title":"电脑上的","body":"电脑正文","tags":["电脑"],"pinned":false,
   "createdAtMs":1000,"updatedAtMs":2000,"deletedAtMs":null},
  {"id":"s2","title":"删掉的","body":"","tags":[],"pinned":false,
   "createdAtMs":1000,"updatedAtMs":3000,"deletedAtMs":3000}
]}
""";
var (syncOk, _, syncResponse) = NoteSyncWire.ParseResponse(200, syncReply);
Assert(syncOk && syncResponse is not null, "a normal sync reply parses");
var parsedSync = syncResponse!;
Assert(parsedSync.ServerTime == 7_777, "the server time is read back for the next watermark");
Assert(parsedSync.Notes.Count == 2, "both notes are read");
Assert(parsedSync.Notes[1].DeletedAtMs == 3_000, "a tombstone is recognised on the way in");

var (forbidden, forbiddenError, _) = NoteSyncWire.ParseResponse(403, """{"success":false,"error":"summary_sync_disabled"}""");
Assert(!forbidden && forbiddenError!.Contains("HTTP 403", StringComparison.Ordinal), "an HTTP error names the status");

var (notJson, notJsonError, _) = NoteSyncWire.ParseResponse(200, "<html>proxy</html>");
Assert(!notJson && notJsonError!.Contains("不是 JSON", StringComparison.Ordinal), "a non-JSON body is reported");

var (noNotes, _, emptyResponse) = NoteSyncWire.ParseResponse(200, """{"serverTime":42}""");
Assert(noNotes && emptyResponse is not null && emptyResponse.Notes.Count == 0,
    "a reply without a notes array is an empty round");
Assert(emptyResponse!.ServerTime == 42, "the watermark still advances");

// ------------------------------------------------------------------ note sync: the watermark

var stateFile = PathFor("notes_sync.json");
var state = new NoteSyncState(stateFile);
Assert(state.LastSyncAt == 0, "a fresh watermark starts at zero");

state.Record(5_555, 4_444);
Assert(state.LastSyncAt == 5_555, "the computer-domain watermark is recorded");
Assert(state.LastPushedAt == 4_444, "the phone-domain watermark is recorded separately");

var reloadedState = new NoteSyncState(stateFile);
Assert(reloadedState.LastSyncAt == 5_555, "the computer watermark survives a restart");
Assert(reloadedState.LastPushedAt == 4_444, "the phone watermark survives a restart");

state.Record(100, 100);
Assert(state.LastSyncAt == 5_555 && state.LastPushedAt == 4_444, "both watermarks only move forward");

File.WriteAllText(PathFor("bad-state.json"), "{ this is not json");
Assert(new NoteSyncState(PathFor("bad-state.json")).LastSyncAt == 0, "a damaged watermark file falls back to zero");

// ------------------------------------------------------------------ note sync: the client

var syncHandler = new SyncHandler((_, _) => (200, syncReply));
var syncClient = new NoteSyncClient(new HttpClient(syncHandler));
var (clientOk, clientError, clientResponse) = await syncClient.SyncAsync(
    "http://127.0.0.1:9899", "phone-9", "tok-1", 10, new[] { wireSource });

Assert(clientOk && clientResponse is not null, "the client parses a good round: " + clientError);
Assert(syncHandler.LastUri?.EndsWith("/notes/sync", StringComparison.Ordinal) == true, "the client posts to /notes/sync");
Assert(syncHandler.LastFromDevice == "phone-9", "the device name travels in the header");
Assert(syncHandler.LastToken == "tok-1", "the pairing token travels in the header");
Assert(syncHandler.LastBody!.Contains("\"since\":10", StringComparison.Ordinal), "the watermark is in the body");
Assert(syncHandler.LastBody!.Contains("\"deletedAtMs\":3000", StringComparison.Ordinal), "tombstones are sent too");

var deniedHandler = new SyncHandler((_, _) => (403, """{"success":false,"error":"summary_sync_disabled"}"""));
var (deniedOk, deniedMessage, _) = await new NoteSyncClient(new HttpClient(deniedHandler))
    .SyncAsync("http://127.0.0.1:9899", "phone-9", "tok-1", 0, Array.Empty<Note>());
Assert(!deniedOk && deniedMessage.Contains("HTTP 403", StringComparison.Ordinal), "a refusal is reported, not swallowed");

var offlineHandler = new SyncHandler((_, _) => throw new HttpRequestException("找不到主机"));
var (offlineOk, offlineMessage, _) = await new NoteSyncClient(new HttpClient(offlineHandler))
    .SyncAsync("http://127.0.0.1:9899", "phone-9", "tok-1", 0, Array.Empty<Note>());
Assert(!offlineOk && offlineMessage.Contains("连不上电脑", StringComparison.Ordinal), "a network failure is reported");

// ------------------------------------------------------------------ note sync: the service

var syncStore = new NoteStore(PathFor("sync-notes.json"));
var syncState = new NoteSyncState(PathFor("sync-watermark.json"));
var settings = new NoteSyncSettings(true, "http://127.0.0.1:9899", "phone-9", "tok-1");
var currentSettings = settings;

NoteSyncService MakeService(SyncHandler handler) =>
    new(syncStore, syncState, new NoteSyncClient(new HttpClient(handler)), () => currentSettings);

var emptyRound = new SyncHandler((_, _) => (200, """{"success":true,"serverTime":9000,"notes":[]}"""));
var notesSyncService = MakeService(emptyRound);

syncStore.Save(new NoteDraft("本地笔记", "手机上写的"));

var firstRound = await notesSyncService.SyncOnceAsync();
Assert(firstRound.Ok, "a round succeeds: " + firstRound.Message);
Assert(firstRound.Pushed == 1, "the local note was pushed");
Assert(syncState.LastSyncAt == 9_000, "the watermark moved to the server time");
Assert(emptyRound.LastBody!.Contains(syncStore.All()[0].Id, StringComparison.Ordinal),
    "the note actually travelled (its id is in the body)");

var secondRound = await notesSyncService.SyncOnceAsync();
Assert(secondRound.Ok && secondRound.Pushed == 0, "a second round sends nothing new");
Assert(emptyRound.LastBody!.Contains("\"since\":9000", StringComparison.Ordinal), "the second round sends the new watermark");

var pullHandler = new SyncHandler((_, _) => (200, """
{"success":true,"serverTime":9100,"notes":[
  {"id":"from-pc","title":"电脑写的","body":"电脑正文","tags":[],"pinned":false,
   "createdAtMs":1000,"updatedAtMs":9100,"deletedAtMs":null}
]}
"""));
var pullService = MakeService(pullHandler);
var pullRound = await pullService.SyncOnceAsync();
Assert(pullRound.Ok && pullRound.Pulled == 1, "a note from the computer is taken");
Assert(syncStore.All().Any(note => note.Id == "from-pc"), "the pulled note is in the store");
Assert(syncStore.All().Any(note => note.Id == "from-pc" && note.Title == "电脑写的"), "its content came across");

// A local deletion must reach the computer as a tombstone.
var toDelete = syncStore.All().First();
Assert(syncStore.Delete(toDelete.Id), "a note can be deleted locally");
var tombstoneHandler = new SyncHandler((_, _) => (200, """{"success":true,"serverTime":9300,"notes":[]}"""));
var tombstoneService = MakeService(tombstoneHandler);
await tombstoneService.SyncOnceAsync();
Assert(tombstoneHandler.LastBody!.Contains("deletedAtMs", StringComparison.Ordinal), "the deletion travels to the computer");

// A deletion on the computer removes the local note.
var remoteDeleteHandler = new SyncHandler((_, _) => (200, """
{"success":true,"serverTime":9400,"notes":[
  {"id":"from-pc","title":"电脑写的","body":"电脑正文","tags":[],"pinned":false,
   "createdAtMs":1000,"updatedAtMs":9400,"deletedAtMs":9400}
]}
"""));
var remoteDeleteService = MakeService(remoteDeleteHandler);
await remoteDeleteService.SyncOnceAsync();
Assert(syncStore.All().All(note => note.Id != "from-pc"), "a deletion from the computer removes the local note");

// A refused round changes nothing.
var beforeFailure = syncStore.Count;
var watermarkBeforeFailure = syncState.LastSyncAt;
var failingSync = new SyncHandler((_, _) => (403, """{"success":false,"error":"summary_sync_disabled"}"""));
var failingService = MakeService(failingSync);
var failure = await failingService.SyncOnceAsync();
Assert(!failure.Ok, "a refused round reports failure");
Assert(failingService.LastError is not null, "the refusal reason is kept");
Assert(syncStore.Count == beforeFailure, "a refused round changes no local note");
Assert(syncState.LastSyncAt == watermarkBeforeFailure, "a refused round does not move the watermark");

// Sync switched off / not paired: no request at all.
var untouchedHandler = new SyncHandler((_, _) => (200, """{"success":true,"serverTime":1,"notes":[]}"""));
currentSettings = settings with { Enabled = false };
var offOutcome = await MakeService(untouchedHandler).SyncOnceAsync();
Assert(!offOutcome.Ok && offOutcome.Message.Contains("未开启", StringComparison.Ordinal), "sync off is reported");
Assert(untouchedHandler.Calls == 0, "sync off sends no request");

currentSettings = settings with { Token = "" };
var unpairedOutcome = await MakeService(untouchedHandler).SyncOnceAsync();
Assert(!unpairedOutcome.Ok && unpairedOutcome.Message.Contains("配对", StringComparison.Ordinal), "an unpaired phone is told so");
Assert(untouchedHandler.Calls == 0, "an unpaired phone sends no request");

currentSettings = settings;
Assert(MakeService(untouchedHandler).Describe().Contains("本机", StringComparison.Ordinal), "the summary line describes local state");

Console.WriteLine();
Console.WriteLine($"{checks} checks passed");

// ------------------------------------------------------------------ fakes

sealed class RecordingSink : INotificationSink
{
    public List<AppNotification> Sent { get; } = new();

    public bool Accept { get; init; } = true;

    public bool IsSupported => true;

    public string? LastError { get; private set; }

    public bool Show(AppNotification notification)
    {
        if (!Accept)
        {
            LastError = "通知被拒绝（测试）";
            return false;
        }

        Sent.Add(notification);
        return true;
    }
}

sealed class RecordingAlarm : IReminderAlarm
{
    public List<string> Scheduled { get; } = new();

    public List<string> Cancelled { get; } = new();

    public bool IsSupported => true;

    public string? LastError => null;

    public string Describe => "测试用闹钟";

    public void Schedule(Reminder reminder) => Scheduled.Add(reminder.Id);

    public void Cancel(string id) => Cancelled.Add(id);

    public void CancelAll()
    {
    }
}

sealed class FakeKnowledge : IKnowledgeSearch
{
    private readonly KnowledgeIndex _index;

    public FakeKnowledge(KnowledgeIndex index) => _index = index;

    public IReadOnlyList<string> Documents => _index.Documents;

    public IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 5) => _index.Search(query, limit);
}

/// <summary>Stands in for the paired computer: answers with a canned status and body, records the request.</summary>
sealed class SyncHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, (int Status, string Body)> _respond;

    public SyncHandler(Func<HttpRequestMessage, string, (int Status, string Body)> respond) => _respond = respond;

    public int Calls { get; private set; }

    public string? LastUri { get; private set; }

    public string? LastBody { get; private set; }

    public string? LastFromDevice { get; private set; }

    public string? LastToken { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastUri = request.RequestUri?.ToString();
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        LastFromDevice = request.Headers.TryGetValues("X-From-Device", out var device) ? string.Join("", device) : null;
        LastToken = request.Headers.TryGetValues("X-Device-Token", out var token) ? string.Join("", token) : null;

        var (status, body) = _respond(request, LastBody ?? "");

        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
