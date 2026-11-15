using System.Text.Json;
using MyAiAssistant.Services;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

var sandbox = Path.Combine(AppContext.BaseDirectory, "sandbox");
if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
Directory.CreateDirectory(sandbox);

string PathFor(string name) => Path.Combine(sandbox, name);

// ------------------------------------------------------------------ the store

var notes = new NoteService(PathFor("notes.json"));

Assert(notes.Count == 0, "a new store is empty");

var first = notes.Create("周会要点", "预算、排期、招聘。", new[] { "工作" });
Assert(notes.Count == 1, "a created note is counted");
Assert(notes.Get(first.Id)?.Content.Contains("预算", StringComparison.Ordinal) == true, "a note can be read back");
Assert(notes.Search("排期").Count == 1, "search finds the note");
Assert(notes.Search("不存在的词").Count == 0, "search that matches nothing returns nothing");

var reopened = new NoteService(PathFor("notes.json"));
Assert(reopened.Count == 1, "notes survive a restart");

// Deleting leaves a tombstone: the note disappears from every read path, the record stays.
Assert(notes.Delete(first.Id), "a note can be deleted");
Assert(notes.Count == 0, "a deleted note is no longer counted");
Assert(notes.Get(first.Id) is null, "a deleted note cannot be read");
Assert(notes.List().Count == 0, "a deleted note is not listed");
Assert(notes.TombstoneCount == 1, "the deletion is kept as a tombstone so it can reach the phone");
Assert(new NoteService(PathFor("notes.json")).TombstoneCount == 1, "the tombstone reached the file");
Assert(!notes.Delete(first.Id), "deleting twice reports nothing to delete");

var revived = notes.Create("回来吧", "不该出现两条", Array.Empty<string>());
Assert(notes.Update(revived.Id, "回来吧（改）", null, null)?.Title == "回来吧（改）", "a note can be updated");

// ------------------------------------------------------------------ change feeds

var watermark = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
System.Threading.Thread.Sleep(20);

var late = notes.Create("水位之后的笔记", "只应该出现在后面的增量里", Array.Empty<string>());
Assert(notes.ChangedSince(watermark).Any(note => note.Id == late.Id), "a note created after the watermark is in the delta");
Assert(notes.ChangedSince(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Count == 0, "nothing changed in the future");

// ------------------------------------------------------------------ remote merge

var store = new NoteService(PathFor("merge.json"));
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

var added = store.ApplyRemoteBatch(new[]
{
    new RemoteNote("remote1", "手机上的笔记", "正文", new[] { "手机" }, now - 1000, now, null),
});
Assert(added == 1 && store.Count == 1, "a note from the phone is added");
Assert(store.Get("remote1")?.Content == "正文", "the body maps to the content field");

Assert(store.ApplyRemoteBatch(new[]
{
    new RemoteNote("remote1", "旧标题", "旧正文", Array.Empty<string>(), now - 1000, now - 500, null),
}) == 0, "an older copy does not overwrite the newer one");
Assert(store.Get("remote1")?.Title == "手机上的笔记", "the newer title survived");

Assert(store.ApplyRemoteBatch(new[]
{
    new RemoteNote("remote1", "新标题", "新正文", Array.Empty<string>(), now - 1000, now + 5000, null),
}) == 1, "a newer copy does overwrite");
Assert(store.Get("remote1")?.Title == "新标题", "the newer title is in place");

Assert(store.ApplyRemoteBatch(new[]
{
    new RemoteNote("remote1", "新标题", "新正文", Array.Empty<string>(), now - 1000, now + 9000, now + 9000),
}) == 1, "a deletion from the phone is applied");
Assert(store.Count == 0, "the note is gone locally too");
Assert(store.TombstoneCount == 1, "and it is a tombstone here, so a third device would learn about it");

var stale = new NoteService(PathFor("prune.json"));
var oldStamp = DateTimeOffset.UtcNow.AddDays(-120).ToUnixTimeMilliseconds();
stale.ApplyRemoteBatch(new[]
{
    new RemoteNote("old1", "早就删掉的", "x", Array.Empty<string>(), oldStamp, oldStamp, oldStamp),
});
Assert(stale.TombstoneCount == 1, "an old tombstone arrives as a tombstone");
Assert(stale.PruneTombstones(TimeSpan.FromDays(90)) == 1, "a tombstone older than the lifetime is dropped");
Assert(stale.TombstoneCount == 0, "no tombstones remain");

// ------------------------------------------------------------------ the phone-facing bridge

var tokens = new PairedDeviceTokens(PathFor("tokens"));
var phoneToken = tokens.Issue("phone-1");

var bridgeStore = new NoteService(PathFor("bridge.json"));
var enabled = true;
var bridge = new PairedNoteBridge(bridgeStore, tokens, () => enabled);

Assert(bridge.RejectReason("phone-1", phoneToken) is null, "a paired device with the switch on is allowed");

enabled = false;
Assert(bridge.RejectReason("phone-1", phoneToken) is not null, "the switch being off rejects the request");
enabled = true;
Assert(bridge.RejectReason("phone-1", "wrong-token") is not null, "a wrong token is rejected");
Assert(bridge.RejectReason(null, null) is not null, "no credentials are rejected");

var pushTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var requestBody = JsonSerializer.Serialize(new
{
    since = 0L,
    device = "phone-1",
    notes = new[]
    {
        new
        {
            id = "n-from-phone",
            title = "手机笔记",
            body = "手机写的正文",
            tags = new[] { "手机" },
            pinned = false,
            createdAtMs = pushTime,
            updatedAtMs = pushTime,
            deletedAtMs = (long?)null,
        },
    },
});

var merged = bridge.Handle(requestBody);
Assert(merged.Ok, "a well-formed round succeeds: " + merged.Message);
Assert(bridgeStore.Count == 1, "the phone's note landed on this machine");

using (var document = JsonDocument.Parse(merged.Json))
{
    var root = document.RootElement;
    Assert(root.GetProperty("success").GetBoolean(), "the answer says success");
    Assert(root.TryGetProperty("serverTime", out var serverTime) && serverTime.GetInt64() > 0,
        "the answer carries a server time for the watermark");

    var returned = root.GetProperty("notes")[0];
    // These are the exact keys the phone parses; renaming one breaks sync silently otherwise.
    Assert(returned.GetProperty("id").GetString() == "n-from-phone", "the answer echoes the note id");
    Assert(returned.GetProperty("title").GetString() == "手机笔记", "the answer carries the title");
    Assert(returned.GetProperty("body").GetString() == "手机写的正文", "the answer carries the body under 'body'");
    Assert(returned.GetProperty("tags")[0].GetString() == "手机", "the answer carries the tags");
    Assert(returned.TryGetProperty("createdAtMs", out _), "the answer carries createdAtMs");
    Assert(returned.TryGetProperty("updatedAtMs", out _), "the answer carries updatedAtMs");
    Assert(returned.TryGetProperty("deletedAtMs", out _), "the answer carries deletedAtMs even when null");
}

long watermarkFromRound;
using (var document = JsonDocument.Parse(merged.Json))
    watermarkFromRound = document.RootElement.GetProperty("serverTime").GetInt64();

var quiet = bridge.Handle(JsonSerializer.Serialize(new { since = watermarkFromRound, notes = Array.Empty<object>() }));
Assert(quiet.Ok, "a second round succeeds");
using (var document = JsonDocument.Parse(quiet.Json))
    Assert(document.RootElement.GetProperty("notes").GetArrayLength() == 0,
        "a round with nothing new returns nothing");

// A desktop-side change made after the watermark must come back to the phone.
var desktopNote = bridgeStore.Create("电脑上的笔记", "电脑写的", Array.Empty<string>());
var pullRound = bridge.Handle(JsonSerializer.Serialize(new { since = watermarkFromRound, notes = Array.Empty<object>() }));
using (var document = JsonDocument.Parse(pullRound.Json))
{
    var notesArray = document.RootElement.GetProperty("notes");
    Assert(notesArray.GetArrayLength() == 1, "the desktop's new note is sent to the phone");
    Assert(notesArray[0].GetProperty("id").GetString() == desktopNote.Id, "it is the right note");
}

// A deletion made on the phone must remove the desktop copy.
var deleteRound = bridge.Handle(JsonSerializer.Serialize(new
{
    since = 0L,
    notes = new[]
    {
        new
        {
            id = desktopNote.Id,
            title = "电脑上的笔记",
            body = "电脑写的",
            tags = Array.Empty<string>(),
            pinned = false,
            createdAtMs = NoteService.ToUnixMs(desktopNote.CreatedAt),
            updatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000,
            deletedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000,
        },
    },
}));
Assert(deleteRound.Ok, "the deletion round succeeds");
Assert(bridgeStore.Get(desktopNote.Id) is null, "the phone's deletion removed the desktop note");

var badJson = bridge.Handle("{ not json");
Assert(!badJson.Ok && badJson.Json.Contains("bad_json", StringComparison.Ordinal), "a broken body is refused with a code");

var tooBig = bridge.Handle(new string('x', PairedNoteBridge.MaxRequestBytes + 10));
Assert(!tooBig.Ok && tooBig.Json.Contains("payload_too_large", StringComparison.Ordinal), "an oversized body is refused");

var noNotesArray = bridge.Handle(JsonSerializer.Serialize(new { since = 0 }));
Assert(noNotesArray.Ok, "a round without a notes array is still a valid (empty) round");

Console.WriteLine();
Console.WriteLine($"{checks} checks passed");
