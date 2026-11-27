using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyAiAssistant.Services;

namespace LocalizedText.Tests;

/// <summary>
/// Pins the user-visible text that was silently corrupted for months: every translation prompt
/// except English arrived at the model as mojibake, so six of the seven languages were broken
/// and nothing failed. The reminder strings sit in the same class of bug, so they are checked
/// here too.
///
/// The check is deliberately structural rather than a copy of the current wording: a prompt must
/// name its own language, must carry the text to translate, must not contain a private-use
/// character (the signature of this particular corruption) and must be free of CJK unless the
/// target language is CJK. Fixing the wording without breaking these is fine; regressing the
/// encoding is not.
/// </summary>
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            PromptChecks().GetAwaiter().GetResult();
            ReminderChecks().GetAwaiter().GetResult();
            Console.WriteLine($"All {_checks} localized-text checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static async Task PromptChecks()
    {
        var captor = new PromptCaptor();
        var translator = new TranslateService(new HttpClient(captor));

        var cases = new (string Code, string Names, bool AllowsCjk, string Label)[]
        {
            ("en", "Translate the following text to English", false, "english prompt"),
            ("zh", "将以下文本翻译为中文", true, "chinese prompt"),
            ("ja", "日本語に翻訳", true, "japanese prompt"),
            ("ko", "한국어로 번역", true, "korean prompt"),
            ("fr", "en français", false, "french prompt"),
            ("de", "Übersetzen", false, "german prompt"),
            ("es", "español", false, "spanish prompt"),
        };

        foreach (var (code, names, allowsCjk, label) in cases)
        {
            var answer = await translator.TranslateAsync("hello world", code, "http://stub/", "test-model", "secret");
            Assert(answer == "stub-reply", $"{label} surfaces the model's reply");

            var prompt = captor.LastPrompt
                ?? throw new Exception($"{label} never reached the endpoint");
            Assert(prompt.Contains(names, StringComparison.Ordinal), $"{label} says what it is for");
            Assert(prompt.EndsWith("\n\nhello world", StringComparison.Ordinal),
                $"{label} carries the text to translate");
            Assert(!prompt.Contains("secret", StringComparison.Ordinal), $"{label} never leaks the api key");

            if (!allowsCjk)
                Assert(!prompt.Any(ch => ch is >= '\u4e00' and <= '\u9fff'),
                    $"{label} carries no stray CJK");

            Assert(!prompt.Any(ch => ch is >= '\ue000' and <= '\uf8ff'),
                $"{label} carries no private-use characters");
            Assert(!HasMojibakeMarkers(prompt), $"{label} carries no double-encoded remnants");
        }
    }

    private static async Task ReminderChecks()
    {
        // The service persists next to the executable; clear it so a previous run cannot
        // influence what is pending.
        var store = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reminders.json");
        if (File.Exists(store)) File.Delete(store);

        string? firedTitle = null;
        var reminders = new ReminderService((title, _) => firedTitle = title);

        reminders.AddReminder("吃药", "饭后半小时", DateTime.Now.AddMinutes(10));

        var setTool = new SetReminderTool(reminders);
        await setTool.ExecuteAsync(new JsonObject());
        Assert(reminders.GetPendingReminders().Any(r => r.Title == "提醒"),
            "a set_reminder call without a title falls back to 提醒");

        var listing = await new ListRemindersTool(reminders).ExecuteAsync(new JsonObject());
        Assert(listing.Contains(" — ", StringComparison.Ordinal),
            "the reminder listing separates title and time with an em dash");
        Assert(listing.Contains("吃药", StringComparison.Ordinal), "the listing shows the reminder");
        Assert(!HasMojibakeMarkers(listing), "the listing is free of double-encoded remnants");

        reminders.AddReminder("过期项", "", DateTime.Now.AddMinutes(-1));
        reminders.Start();
        Assert(firedTitle is not null && firedTitle.StartsWith("提醒: ", StringComparison.Ordinal),
            "a fired reminder names itself in Chinese");

        reminders.Stop();
        if (File.Exists(store)) File.Delete(store);
    }

    private static bool HasMojibakeMarkers(string text)
    {
        string[] markers =
        [
            "鈥", "鎻", "愰", "啋", "灏", "嗕", "浠ヤ", "雼", "锟",
            "銆", "鏈", "璇戜", "负涓", "缈昏", "莽", "脺", "眉", "帽", "贸", "ǔ"
        ];
        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Captures the prompt body the service posts, and answers like a chat endpoint.</summary>
    private sealed class PromptCaptor : HttpMessageHandler
    {
        public string? LastPrompt { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            LastPrompt = document.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString();

            const string reply = """{"choices":[{"message":{"content":"stub-reply"}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json")
            });
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
}
