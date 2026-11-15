using System.IO;
using System.Net.Http;
using System.Text.Json;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.Localization;
using MyAiAssistant.Services.Messaging;
using MyAiAssistant.Services.Permissions;

namespace Catalog.Tests;

/// <summary>
/// Checks the catalogues against the real assembly: providers the UI offers, the platforms the
/// registry declares, and the strings the localiser can resolve.
/// </summary>
internal static class Program
{
    private static int _checks;

    private static async Task<int> Main()
    {
        try
        {
            var names = ConfigManager.GetProviderNames();
            Assert(names.Count >= 16, "at least thirteen providers offered: " + names.Count);

            foreach (var expected in new[] { "Kimi", "DeepSeek", "MiMo", "OpenAI", "Ollama", "OpenRouter", "Groq", "Mistral", "xAI", "Google Gemini", "智谱 GLM", "通义千问", "硅基流动", "火山方舟", "NVIDIA NIM", "零一万物" })
                Assert(names.Contains(expected), $"provider {expected} offered");

            Assert(names.Distinct().Count() == names.Count, "provider names are unique");

            foreach (var name in names.Where(n => n != "自定义平台"))
            {
                var info = ConfigManager.GetProviderInfo(name);
                Assert(info is not null, $"{name} resolves");
                Assert(info!.Value.BaseUrl.StartsWith("http", StringComparison.Ordinal), $"{name} has an http endpoint");
                Assert(!string.IsNullOrWhiteSpace(info.Value.Model), $"{name} has a default model");
            }

            var registry = new MessagingPlatformRegistry(new IMessagingAdapter[]
            {
                new TelegramAdapter(null),
                new QQAdapter(),
                new QQOfficialAdapter(() => null, () => null)
            });
            Assert(registry.Platforms.Count == 17, "seven platforms declared");
            Assert(registry.Platforms.All(p => !string.IsNullOrWhiteSpace(p.Requirement)), "every platform names its requirement");
            Assert(registry.Platforms.First(p => p.Kind == PlatformKind.QQ).Capabilities.GroupChat, "QQ supports group chat");
            Assert(registry.Platforms.First(p => p.Kind == PlatformKind.WeChat).State == ImplementationState.Planned,
                "personal WeChat is not claimed as implemented");

            var zh = new Localizer("zh-CN");
            var en = new Localizer("en-US");
            foreach (var platform in registry.Platforms)
                Assert(!string.IsNullOrWhiteSpace(zh[$"messaging.state.{platform.State.ToString().ToLowerInvariant()}"]),
                    $"localised state for {platform.State}");
            Assert(en["common.save"] == "Save" && zh["common.save"] == "保存", "both languages resolve");
            Assert(new Localizer("de-DE").Language == Localizer.DefaultLanguage, "unknown language falls back");


            // --- accounts: dropdown source, multi-account add, probe, remove ---------
            {
                var config = new AppConfig();
                var store = new MessagingAccountStore(() => config, c => config = c);
                var telegramHttp = new HttpClient(new TelegramStub()) { Timeout = TimeSpan.FromSeconds(5) };
                var accountService = new MessagingAccountService(store, _ => telegramHttp);

                var auditDir = Path.Combine(Path.GetTempPath(), "fairyai-catalog-" + Guid.NewGuid().ToString("N"));
                var audit = new OperationAudit(auditDir);
                const string token = "catalog-token";
                var api = new DashboardApi(new AgentControlCenter(), audit, token,
                    settings: null, accounts: store, accountService: accountService);

                using (var doc = JsonDocument.Parse(api.Get("/api/platforms", token).Body))
                {
                    var platforms = doc.RootElement.GetProperty("platforms");
                    Assert(platforms.GetArrayLength() == 17, "platform catalogue exposed for the dropdown");
                    var implemented = platforms.EnumerateArray().Count(p => p.GetProperty("implemented").GetBoolean());
                    Assert(implemented == 3, "only two platforms are selectable");
                }

                Assert(api.Get("/api/accounts", token).StatusCode == 200, "accounts listed");

                var added = api.Post("/api/accounts", token,
                    "{\"platform\":\"Telegram\",\"displayName\":\"主号\",\"token\":\"123:secret-token\"}");
                Assert(added.StatusCode == 200, "telegram account added");
                Assert(!added.Body.Contains("123:secret-token", StringComparison.Ordinal), "token never returned");

                using (var doc = JsonDocument.Parse(api.Get("/api/accounts", token).Body))
                {
                    var accounts = doc.RootElement.GetProperty("accounts");
                    Assert(accounts.GetArrayLength() == 1, "one account stored");
                    Assert(accounts[0].GetProperty("missingFields").GetArrayLength() == 0, "no required credential is missing");
                }

                Assert(api.Post("/api/accounts", token,
                    "{\"platform\":\"Telegram\",\"displayName\":\"主号\"}").StatusCode == 409, "duplicate name rejected");
                Assert(api.Post("/api/accounts", token,
                    "{\"platform\":\"Discord\",\"displayName\":\"x\"}").StatusCode == 409, "unimplemented platform rejected");

                var id = store.List()[0].Id;
                var probe = await api.PostAsync($"/api/accounts/{id}/probe", token, null);
                Assert(probe.StatusCode == 200 && probe.Body.Contains("Ready", StringComparison.Ordinal),
                    "probe reports ready for a valid telegram token");

                Assert(api.Post($"/api/accounts/{id}/remove", token, null).StatusCode == 200, "account removed");
                Assert(store.List().Count == 0, "account list empty after removal");

                try { Directory.Delete(auditDir, true); } catch (IOException) { }
            }

            Console.WriteLine($"All {_checks} catalog checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    private sealed class TelegramStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"username\":\"fairy_bot\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

}