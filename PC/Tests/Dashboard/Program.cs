using System.Text.Json;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.Messaging;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

async Task WaitUntil(Func<bool> condition, string name, int timeoutMs = 2000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition()) { checks++; return; }
        await Task.Delay(10);
    }
    throw new Exception("TIMEOUT: " + name);
}

var directory = Path.Combine(Path.GetTempPath(), "fairyai-dashboard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var center = new AgentControlCenter();
    var audit = new OperationAudit(directory);
    const string token = "test-token-0123456789abcdef";
    var store = new FakeSettingsStore();
    var api = new DashboardApi(center, audit, token, store);

    // 1. Tasks are the real ones, not placeholder numbers.
    var task = center.Enqueue("索引知识库", "Pi", "mimo");
    task.Start();
    task.ReportProgress(1, 4, "读取文档");
    Assert(api.Get("/api/tasks", null).StatusCode == 401, "task list is not readable without the token");
    var tasksResponse = api.Get("/api/tasks", token);
    Assert(tasksResponse.StatusCode == 200, "tasks endpoint ok");
    using (var doc = JsonDocument.Parse(tasksResponse.Body))
    {
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert(tasks.GetArrayLength() == 1, "one real task reported");
        var first = tasks[0];
        Assert(first.GetProperty("title").GetString() == "索引知识库", "title is the enqueued one");
        Assert(first.GetProperty("state").GetString() == "Running", "state is real");
        Assert(first.GetProperty("step").GetInt32() == 1 && first.GetProperty("totalSteps").GetInt32() == 4, "progress is real");
        Assert(first.GetProperty("canPause").GetBoolean(), "capability flags exposed");
    }

    // 2. Resources are measured, not fabricated.
    using (var doc = JsonDocument.Parse(api.Get("/api/resources", token).Body))
    {
        Assert(doc.RootElement.GetProperty("workingSetBytes").GetInt64() > 0, "working set measured");
        Assert(doc.RootElement.GetProperty("threadCount").GetInt32() > 0, "thread count measured");
    }

    // 3. Audit needs the token because it contains paths.
    Assert(api.Get("/api/audit", null).StatusCode == 401, "audit without token rejected");
    Assert(api.Get("/api/audit", "wrong").StatusCode == 401, "audit with wrong token rejected");
    Assert(api.Get("/api/audit", token).StatusCode == 200, "audit with token allowed");

    // 4. Control without a token must not change anything.
    var unauthorised = api.Post($"/api/tasks/{task.Id}/pause", null, null);
    Assert(unauthorised.StatusCode == 401, "control without token rejected");
    Assert(!center.Task(task.Id)!.PauseRequested, "rejected control changed nothing");

    Assert(api.Post($"/api/tasks/{task.Id}/pause", "nope", null).StatusCode == 401, "control with wrong token rejected");
    Assert(!center.Task(task.Id)!.PauseRequested, "wrong token changed nothing");

    // 5. A token with a different length must be refused without throwing.
    Assert(api.Post($"/api/tasks/{task.Id}/pause", token + "x", null).StatusCode == 401, "longer token rejected");

    // 6. Authorised control drives the real state machine.
    Assert(api.Post($"/api/tasks/{task.Id}/pause", token, null).StatusCode == 200, "pause accepted");
    Assert(center.Task(task.Id)!.PauseRequested, "pause request reached the task");

    var waiting = task.WaitIfPausedAsync();
    await WaitUntil(() => task.State == AgentTaskState.Paused, "task parked at the boundary");

    Assert(api.Post($"/api/tasks/{task.Id}/resume", token, null).StatusCode == 200, "resume accepted");
    await waiting;
    Assert(task.State == AgentTaskState.Running, "task resumed");

    Assert(api.Post($"/api/tasks/{task.Id}/stop", token, null).StatusCode == 200, "stop accepted");
    Assert(center.Task(task.Id)!.State == AgentTaskState.Cancelled, "task cancelled");

    // 7. Illegal transitions come back as a conflict, not a silent success.
    Assert(api.Post($"/api/tasks/{task.Id}/pause", token, null).StatusCode == 409, "pause on cancelled conflicts");
    Assert(api.Post($"/api/tasks/{task.Id}/stop", token, null).StatusCode == 409, "repeated stop conflicts");
    Assert(api.Post("/api/tasks/missing/pause", token, null).StatusCode == 409, "unknown task conflicts");

    // 8. Approvals raised by the gate appear on the dashboard and can be answered from it.
    var gateTask = center.Enqueue("清理缓存");
    gateTask.Start();
    var approval = gateTask.AskApprovalAsync("删除 30 个缓存文件？", RiskCategory.DeleteOrOverwriteData);
    await WaitUntil(() => center.Pending().Count == 1, "approval visible on dashboard");

    using (var doc = JsonDocument.Parse(api.Get("/api/pending", token).Body))
    {
        var pending = doc.RootElement.GetProperty("pending");
        Assert(pending.GetArrayLength() == 1, "pending reported");
        Assert(pending[0].GetProperty("kind").GetString() == "Approval", "kind reported");
        Assert(pending[0].GetProperty("riskText").GetString() == "删除或覆盖数据", "risk rendered in Chinese");
    }

    var interactionId = center.Pending()[0].Id;
    Assert(api.Post($"/api/interactions/{interactionId}/answer", null, "{\"approved\":true}").StatusCode == 401,
        "answering without token rejected");
    Assert(!approval.IsCompleted, "unauthorised answer did not release the gate");

    Assert(api.Post($"/api/interactions/{interactionId}/answer", token, "{\"approved\":false}").StatusCode == 200,
        "authorised answer accepted");
    Assert(!await approval, "dashboard rejection reached the gate");

    // 9. Text answers and malformed bodies.
    var question = gateTask.AskQuestionAsync("目标目录？");
    await WaitUntil(() => center.Pending().Count == 1, "question pending");
    var questionId = center.Pending()[0].Id;
    Assert(api.Post($"/api/interactions/{questionId}/answer", token, "{\"text\":\"D:\\\\out\"}").StatusCode == 200, "text answer accepted");
    Assert(await question == "D:\\out", "text delivered to the task");

    Assert(api.Post("/api/interactions/whatever/answer", token, "not json").StatusCode == 400, "malformed body rejected");
    Assert(api.Post("/api/interactions/whatever/answer", token, "{}").StatusCode == 400, "empty body rejected");
    Assert(api.Post("/api/interactions/whatever/answer", token, "{\"approved\":true}").StatusCode == 409,
        "answer for unknown interaction conflicts");

    // 10. Unknown routes and events stream.
    Assert(api.Get("/api/nothing", token).StatusCode == 404, "unknown route not found");
    using (var doc = JsonDocument.Parse(api.Get("/api/events", token).Body))
        Assert(doc.RootElement.GetProperty("events").GetArrayLength() > 0, "events stream exposed");

    // 10b. Session probe backs the login screen.
    Assert(api.Get("/api/session", null).StatusCode == 401, "session probe requires token");
    Assert(api.Get("/api/session", "bad").StatusCode == 401, "session probe rejects wrong token");
    Assert(api.Get("/api/session", token).StatusCode == 200, "session probe accepts the token");

    // 10c. Settings are readable only with the token, and never contain a stored key.
    Assert(api.Get("/api/settings", null).StatusCode == 401, "settings require token");
    var settingsBody = api.Get("/api/settings", token).Body;
    using (var doc = JsonDocument.Parse(settingsBody))
    {
        var llm = doc.RootElement.GetProperty("llm");
        Assert(llm.GetProperty("baseUrl").GetString() == "https://api.moonshot.cn/v1/chat/completions", "base url exposed");
        Assert(llm.GetProperty("hasApiKey").GetBoolean(), "key presence exposed as a boolean");
        Assert(doc.RootElement.GetProperty("personalityName").GetString() == "Fairy", "personality exposed");
    }
    Assert(!settingsBody.Contains("\"apiKey\"", StringComparison.Ordinal), "settings never carry the key field");
    Assert(!settingsBody.Contains("sk-secret", StringComparison.Ordinal), "stored key never leaves the process");

    // 10d. A partial update only touches the supplied fields.
    var partial = api.Post("/api/settings", token, "{\"llm\":{\"baseUrl\":\"https://example.com/v1/chat/completions\"}}");
    Assert(partial.StatusCode == 200, "partial settings update accepted");
    Assert(store.Updates == 1, "store updated once");
    Assert(store.Last!.Llm!.BaseUrl == "https://example.com/v1/chat/completions", "base url applied");
    Assert(store.Last.Llm.Model is null && store.Last.Asr is null, "unsupplied fields left alone");

    // 10e. Invalid or empty input is refused without touching the store.
    Assert(api.Post("/api/settings", token, "{\"llm\":{\"baseUrl\":\"not a url\"}}").StatusCode == 400, "invalid base url rejected");
    Assert(api.Post("/api/settings", token, "{}").StatusCode == 400, "empty update rejected");
    Assert(api.Post("/api/settings", token, "garbage").StatusCode == 400, "malformed update rejected");
    Assert(api.Post("/api/settings", null, "{\"llm\":{\"model\":\"x\"}}").StatusCode == 401, "settings write requires token");
    Assert(store.Updates == 1, "rejected writes never reached the store");

    // 10f. Writing a credential is recorded in the audit trail.
    Assert(api.Post("/api/settings", token, "{\"llm\":{\"apiKey\":\"sk-new-key\"}}").StatusCode == 200, "credential write accepted");
    Assert(store.Last!.Llm!.ApiKey == "sk-new-key", "credential forwarded to the store");
    var auditTail = audit.Query(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5))
        .LastOrDefault(r => r.ActionId == "settings.update");
    Assert(auditTail is not null && auditTail.Category == RiskCategory.ReadWriteCredentials, "credential write audited");
    Assert(!File.ReadAllText(audit.LogPath).Contains("sk-new-key", StringComparison.Ordinal), "credential never written to the audit log");

    // 11. Real HTTP round trip: the socket layer, the page and the token check over the wire.
    var httpTask = center.Enqueue("HTTP 往返");
    httpTask.Start();
    MyAiAssistant.Services.WebDashboardService? service = null;
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    for (var attempt = 0; attempt < 5 && service is null; attempt++)
    {
        var candidate = new MyAiAssistant.Services.WebDashboardService(center, audit, store, port: 18000 + Random.Shared.Next(1, 900));
        if (await candidate.StartAsync()) service = candidate;
        else candidate.Stop();
    }

    if (service is null)
    {
        // Never pretend the transport works when it could not be started.
        Console.WriteLine("SKIPPED: 无法在 loopback 上启动本地 HTTP 服务，HTTP 层未验证。");
    }
    else
    {
        try
        {
            using (var page = await client.GetAsync(service.Url))
            {
                Assert(page.IsSuccessStatusCode, "page served");
                var html = await page.Content.ReadAsStringAsync();
                Assert(html.Contains("Agent 控制中心", StringComparison.Ordinal), "page renders the control centre");
                Assert(html.Contains("/api/tasks", StringComparison.Ordinal), "page talks to the real api");
                Assert(html.Contains("配对设备组", StringComparison.Ordinal), "page renders the pairing section");
                Assert(html.Contains("id=\"prGroups\"", StringComparison.Ordinal), "the pairing group table is in the page");
                Assert(html.Contains("loadPairing", StringComparison.Ordinal), "the page wires the pairing loader");
                Assert(html.Contains("跨设备传输", StringComparison.Ordinal), "page renders the transfer section");
                Assert(html.Contains("id=\"trPeer\"", StringComparison.Ordinal), "the transfer target picker is in the page");
                Assert(html.Contains("id=\"ntItems\"", StringComparison.Ordinal), "page renders the notification history");
                Assert(html.Contains("loadNotifications", StringComparison.Ordinal), "the page wires the notification loader");
                Assert(html.Contains("Home Assistant", StringComparison.Ordinal), "page renders the home assistant section");
                Assert(html.Contains("id=\"haEntities\"", StringComparison.Ordinal), "the entity table is in the page");
                Assert(html.Contains("loadHomeAssistant", StringComparison.Ordinal), "the page wires the home assistant loader");
                Assert(html.Contains("id=\"ssDevices\"", StringComparison.Ordinal), "page renders the summary-sync device table");
                Assert(html.Contains("loadSummarySync", StringComparison.Ordinal), "the page wires the summary-sync loader");
                Assert(html.Contains("id=\"scPauseBtn\"", StringComparison.Ordinal), "page renders the screen pause button");
                Assert(html.Contains("loadScreenSession", StringComparison.Ordinal), "the page wires the screen session loader");
                Assert(html.Contains("id=\"mcpStartBtn\"", StringComparison.Ordinal), "page renders the MCP start button");
                Assert(html.Contains("loadMcp", StringComparison.Ordinal), "the page wires the MCP loader");
            }

            using (var anonymous = await client.GetAsync(service.Url + "api/tasks"))
            {
                Assert(anonymous.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                    "task data is refused over http without the token");
            }

            using (var anonymousPairing = await client.GetAsync(service.Url + "api/pairing"))
            {
                Assert(anonymousPairing.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                    "pairing state is refused over http without the token");
            }

            using (var tasksRequest = new HttpRequestMessage(HttpMethod.Get, service.Url + "api/tasks"))
            {
                tasksRequest.Headers.Add("X-Fairy-Token", service.ControlToken);
                using var tasks = await client.SendAsync(tasksRequest);
                Assert(tasks.IsSuccessStatusCode, "tasks over http");
                Assert((await tasks.Content.ReadAsStringAsync()).Contains("HTTP 往返", StringComparison.Ordinal), "http returns live data");
            }

            using (var unauthorisedHttp = await client.PostAsync(service.Url + $"api/tasks/{httpTask.Id}/stop", null))
            {
                Assert(unauthorisedHttp.StatusCode == System.Net.HttpStatusCode.Unauthorized, "http control requires token");
            }
            Assert(httpTask.State == AgentTaskState.Running, "rejected http control changed nothing");

            using (var request = new HttpRequestMessage(HttpMethod.Post, service.Url + $"api/tasks/{httpTask.Id}/stop"))
            {
                request.Headers.Add("X-Fairy-Token", service.ControlToken);
                using var authorised = await client.SendAsync(request);
                Assert(authorised.IsSuccessStatusCode, "http control with token accepted");
            }
            Assert(httpTask.State == AgentTaskState.Cancelled, "http control reached the state machine");
        }
        finally
        {
            service.Stop();
        }
    }

    // 12. Fairy Code routes: token, honest engine state, and refusals with reasons.
    {
        Assert(api.Get("/api/fairycode", token).StatusCode == 503,
            "without a Fairy Code manager the endpoint says unavailable instead of pretending");

        var fairyApi = new DashboardApi(center, audit, token, store, null, null,
            new MyAiAssistant.Services.FairyCode.FairyCodeSessionManager(
                center,
                new ApprovalGate(new PermissionPolicy(), audit),
                audit,
                new StubModels(),
                new StubEngine()));

        Assert(fairyApi.Get("/api/fairycode", null).StatusCode == 401, "Fairy Code sessions need the token");
        var sessions = fairyApi.Get("/api/fairycode", token);
        Assert(sessions.StatusCode == 200, "Fairy Code sessions readable with the token");
        using (var doc = JsonDocument.Parse(sessions.Body))
        {
            Assert(doc.RootElement.GetProperty("sessions").GetArrayLength() == 0, "no sessions before any start");
        }

        var probe = await fairyApi.PostAsync("/api/fairycode/probe", token, "{}");
        Assert(probe.StatusCode == 200, "probe answers");
        using (var doc = JsonDocument.Parse(probe.Body))
        {
            Assert(!doc.RootElement.GetProperty("ready").GetBoolean(), "an unavailable engine is not reported ready");
            Assert(doc.RootElement.GetProperty("detail").GetString()!.Contains("Pi", StringComparison.Ordinal),
                "the probe says which piece is missing");
        }

        Assert((await fairyApi.PostAsync("/api/fairycode/start", null, "{\"prompt\":\"x\"}")).StatusCode == 401,
            "starting a task requires the token");
        Assert((await fairyApi.PostAsync("/api/fairycode/start", token, "{}")).StatusCode == 400,
            "a start without a prompt is rejected");
        var refused = await fairyApi.PostAsync("/api/fairycode/start", token,
            "{\"prompt\":\"do it\",\"workspace\":\"" + JsonEncodedText.Encode(Path.Combine(directory, "missing")).Value + "\"}");
        Assert(refused.StatusCode == 409, "a start in a missing workspace is refused, not queued");
        Assert((await fairyApi.PostAsync("/api/fairycode/sessions/nope/stop", token, "{}")).StatusCode == 409,
            "stopping an unknown session is refused");

        var emptyManagerApi = new DashboardApi(center, audit, token, store, null, null,
            new MyAiAssistant.Services.FairyCode.FairyCodeSessionManager(
                center,
                new ApprovalGate(new PermissionPolicy(), audit),
                audit,
                new StubModels(),
                new StubEngine()));
        Assert((await emptyManagerApi.PostAsync("/api/fairycode/start", token, "{}")).StatusCode == 400,
            "an empty start body is a client error");
    }

    // 13. LAN receiving settings: token, honest state, and a refusal that keeps the switch off.
    {
        Assert(api.Get("/api/localsend", token).StatusCode == 503,
            "without the settings service the endpoint says unavailable");

        var localSend = new FakeLocalSendSettings();
        var localApi = new DashboardApi(center, audit, token, store, null, null, null, localSend);

        Assert(localApi.Get("/api/localsend", null).StatusCode == 401, "LAN receive state needs the token");

        var state = localApi.Get("/api/localsend", token);
        Assert(state.StatusCode == 200, "LAN receive state is readable with the token");
        using (var document = JsonDocument.Parse(state.Body))
        {
            Assert(!document.RootElement.GetProperty("enabled").GetBoolean(), "it starts disabled");
            Assert(!document.RootElement.GetProperty("running").GetBoolean(), "nothing is listening yet");
            Assert(document.RootElement.GetProperty("alias").GetString() == "仙灵", "the alias is reported");
        }

        var enabled = await localApi.PostAsync("/api/localsend", token, "{\"enabled\":true}");
        Assert(enabled.StatusCode == 200, "turning the receiver on is accepted");
        Assert(localSend.Applied == (true, null, null), "the toggle reaches the settings service");

        var badAlias = await localApi.PostAsync("/api/localsend", token, "{\"alias\":\"" + new string('x', 41) + "\"}");
        Assert(badAlias.StatusCode == 409, "a settings refusal is reported as a conflict, not as success");

        Assert((await localApi.PostAsync("/api/localsend", token, "{}")).StatusCode == 400,
            "an empty update is a client error");
        Assert((await localApi.PostAsync("/api/localsend", token, "not json")).StatusCode == 400,
            "malformed JSON is refused");
        Assert((await localApi.PostAsync("/api/localsend", token, "{\"enabled\":\"yes\"}")).StatusCode == 400,
            "a non-boolean toggle is refused");
        Assert((await localApi.PostAsync("/api/localsend", null, "{\"enabled\":true}")).StatusCode == 401,
            "changing the setting requires the token");
    }

    // 14. LAN sending: peers come from discovery, and a send result is reported honestly.
    {
        var localSend = new FakeLocalSendSettings();
        var peers = new FakeLocalSendPeers();
        var sendApi = new DashboardApi(center, audit, token, store, null, null, null, localSend, peers);

        var state = sendApi.Get("/api/localsend", token);
        using (var document = JsonDocument.Parse(state.Body))
        {
            Assert(document.RootElement.GetProperty("discoveryRunning").GetBoolean(), "discovery state is exposed");
            var list = document.RootElement.GetProperty("peers");
            Assert(list.GetArrayLength() == 1, "discovered peers are listed");
            Assert(list[0].GetProperty("alias").GetString() == "Pixel", "the peer alias is reported");
            Assert(list[0].GetProperty("endpoint").GetString()!.Contains("53317", StringComparison.Ordinal),
                "the peer endpoint is usable");
        }

        Assert((await sendApi.PostAsync("/api/localsend/send", null, "{}")).StatusCode == 401,
            "sending requires the token");
        Assert((await sendApi.PostAsync("/api/localsend/send", token, "{}")).StatusCode == 400,
            "a send without a target is refused");
        Assert((await sendApi.PostAsync("/api/localsend/send", token,
                "{\"fingerprint\":\"fp-1\"}")).StatusCode == 400,
            "a send without files is refused");

        var sent = await sendApi.PostAsync("/api/localsend/send", token,
            "{\"fingerprint\":\"fp-1\",\"paths\":[\"D:\\\\a.png\",\"D:\\\\b.png\"]}");
        Assert(sent.StatusCode == 200, "a send to a known peer succeeds");
        using (var document = JsonDocument.Parse(sent.Body))
        {
            Assert(document.RootElement.GetProperty("uploadedFiles").GetInt32() == 2, "the file count is reported");
        }

        var gone = await sendApi.PostAsync("/api/localsend/send", token,
            "{\"fingerprint\":\"gone\",\"paths\":[\"D:\\\\a.png\"]}");
        Assert(gone.StatusCode == 409, "a send to a peer that is no longer there is a conflict, not success");
    }

    // 15. Pairing: the code shows once, the group key never appears, and every action needs the token.
    {
        Assert(api.Get("/api/pairing", token).StatusCode == 503,
            "without a pairing service the endpoint says unavailable");

        var pairing = new FakePairingControl();
        var pairingApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, pairing);

        Assert(pairingApi.Get("/api/pairing", null).StatusCode == 401, "pairing state needs the token");

        var view = pairingApi.Get("/api/pairing", token);
        Assert(view.StatusCode == 200, "pairing state is readable with the token");
        using (var document = JsonDocument.Parse(view.Body))
        {
            var root = document.RootElement;
            Assert(!root.GetProperty("enabled").GetBoolean(), "pairing starts off");
            Assert(!root.GetProperty("listening").GetBoolean(), "nothing is listening at first");
            Assert(root.GetProperty("deviceId").GetString() == "DEVFAKE0001", "the device id is reported");
            Assert(root.GetProperty("activeGroupId").GetString() == "GRPHOME", "the active group is reported");
            Assert(root.GetProperty("capabilities").GetArrayLength() > 0, "the capability catalogue is published");

            var groups = root.GetProperty("groups");
            Assert(groups.GetArrayLength() == 1, "groups are listed");
            Assert(groups[0].GetProperty("keyEpoch").GetInt32() == 2, "the key epoch is exposed");
            Assert(groups[0].GetProperty("keyFingerprint").GetString() == "AAAA-BBBB-CCCC", "the key fingerprint is exposed");
            Assert(groups[0].GetProperty("active").GetBoolean(), "the current group is marked");

            var members = groups[0].GetProperty("members");
            Assert(members.GetArrayLength() == 1, "members are listed");
            Assert(members[0].GetProperty("displayName").GetString() == "手机", "the member label is reported");
            Assert(members[0].GetProperty("capabilities").GetArrayLength() == 0,
                "a freshly paired device shows no capabilities");

            // The whole payload must not contain anything that could reconstruct the group key.
            Assert(!view.Body.Contains("groupKey", StringComparison.OrdinalIgnoreCase),
                "the payload never mentions a group key");
            Assert(!view.Body.Contains("wrapped", StringComparison.OrdinalIgnoreCase),
                "the payload never carries wrapped key material");
        }

        var invite = await pairingApi.PostAsync("/api/pairing/invites", token, "{\"groupId\":\"GRPHOME\"}");
        Assert(invite.StatusCode == 200, "an invite can be created");
        using (var document = JsonDocument.Parse(invite.Body))
        {
            Assert(document.RootElement.GetProperty("code").GetString() == "123456",
                "the code is returned exactly once, by the call that created it");
        }

        var refusedInvite = await pairingApi.PostAsync("/api/pairing/invites", token, "{\"groupId\":\"GRPNONE\"}");
        Assert(refusedInvite.StatusCode == 409, "an unknown group is refused as a conflict");
        Assert(!refusedInvite.Body.Contains("code", StringComparison.Ordinal),
            "a refused invite returns no code");

        Assert((await pairingApi.PostAsync("/api/pairing/groups", token, "{}")).StatusCode == 400,
            "creating a group without a name is a client error");
        Assert((await pairingApi.PostAsync("/api/pairing/groups", token, "{\"name\":\"家用\"}")).StatusCode == 200,
            "a group can be created");
        Assert(pairing.CreatedGroup == "家用", "the name reaches the service");

        Assert((await pairingApi.PostAsync("/api/pairing/settings", token, "{\"enabled\":true}")).StatusCode == 200,
            "turning pairing on is accepted");
        Assert(pairing.Applied == (true, null, null), "the toggle reaches the service");
        Assert((await pairingApi.PostAsync("/api/pairing/settings", null, "{\"enabled\":true}")).StatusCode == 401,
            "turning pairing on requires the token");
        Assert((await pairingApi.PostAsync("/api/pairing/settings", token, "{\"port\":80}")).StatusCode == 409,
            "a privileized port is refused as a conflict");
        Assert((await pairingApi.PostAsync("/api/pairing/settings", token, "{}")).StatusCode == 400,
            "an empty settings update is a client error");

        var grant = await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/members/DEVPIXEL/grant", token,
            "{\"capability\":\"FileTransfer\"}");
        Assert(grant.StatusCode == 200, "a capability can be granted through the API");
        Assert(pairing.LastGrant == ("GRPHOME", "DEVPIXEL", "FileTransfer"), "the grant reaches the service with its arguments");

        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/members/DEVPIXEL/grant", null,
                "{\"capability\":\"FileTransfer\"}")).StatusCode == 401,
            "granting a capability requires the token");
        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/members/DEVPIXEL/grant", token,
                "{\"capability\":\"Nonsense\"}")).StatusCode == 409,
            "an unknown capability is refused as a conflict");

        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/members/DEVPIXEL/remove", token, null)).StatusCode == 200,
            "a member can be removed");
        Assert(pairing.LastRemoved == ("GRPHOME", "DEVPIXEL"), "removal reaches the service");
        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/rotate", token, null)).StatusCode == 200,
            "the group key can be rotated");
        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/active", token, null)).StatusCode == 200,
            "the active group can be switched");
        Assert((await pairingApi.PostAsync("/api/pairing/invites/INVX/cancel", token, null)).StatusCode == 200,
            "an invite can be cancelled");
        Assert((await pairingApi.PostAsync("/api/pairing/groups/GRPHOME/unknown", token, null)).StatusCode == 404,
            "an unknown pairing route is a 404");
    }

    // 16. Transfers: paired devices are the targets, and every send needs the token and arguments.
    {
        Assert(api.Get("/api/transfer", token).StatusCode == 503,
            "without a transfer service the endpoint says unavailable");

        var transfer = new FakeTransferControl();
        var transferApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, transfer);

        Assert(transferApi.Get("/api/transfer", null).StatusCode == 401, "transfer state needs the token");

        var view = transferApi.Get("/api/transfer", token);
        Assert(view.StatusCode == 200, "transfer state is readable with the token");
        using (var document = JsonDocument.Parse(view.Body))
        {
            var root = document.RootElement;
            Assert(!root.GetProperty("enabled").GetBoolean(), "transfers start disabled");
            Assert(root.GetProperty("destinationRoot").GetString() == @"D:\received", "the destination is reported");
            Assert(root.GetProperty("hostDeviceId").GetString() == "DEVHOST0001", "the host device is reported");

            var peers = root.GetProperty("peers");
            Assert(peers.GetArrayLength() == 1, "paired devices are offered as targets");
            Assert(peers[0].GetProperty("displayName").GetString() == "平板", "the peer label is reported");
            Assert(peers[0].GetProperty("groupName").GetString() == "家用", "the peer's group is reported");

            Assert(root.GetProperty("jobs").GetArrayLength() == 1, "recent transfers are listed");
            Assert(root.GetProperty("received").GetArrayLength() == 1, "received files are listed");

            // Nothing about frames or keys may leak through this surface.
            Assert(!view.Body.Contains("groupKey", StringComparison.OrdinalIgnoreCase), "no key material is exposed");
            Assert(!view.Body.Contains("sealed", StringComparison.OrdinalIgnoreCase), "no sealed payloads are exposed");
        }

        Assert((await transferApi.PostAsync("/api/transfer/send", null, "{}")).StatusCode == 401,
            "sending requires the token");
        Assert((await transferApi.PostAsync("/api/transfer/send", token, "{}")).StatusCode == 400,
            "a send without a device is a client error");
        Assert((await transferApi.PostAsync("/api/transfer/send", token, "{\"deviceId\":\"DEVPIXEL\"}")).StatusCode == 400,
            "a send without files is a client error");
        Assert((await transferApi.PostAsync("/api/transfer/send", token, "not json")).StatusCode == 400,
            "malformed JSON is refused");

        var sent = await transferApi.PostAsync("/api/transfer/send", token,
            "{\"deviceId\":\"DEVPIXEL\",\"paths\":[\"D:\\\\a.png\",\"D:\\\\b.png\"]}");
        Assert(sent.StatusCode == 200, $"a valid send reaches the service: {sent.Body}");
        Assert(transfer.LastSend == ("DEVPIXEL", 2), "the send carries the device and both paths");

        var refused = await transferApi.PostAsync("/api/transfer/send", token,
            "{\"deviceId\":\"GONE\",\"paths\":[\"D:\\\\a.png\"]}");
        Assert(refused.StatusCode == 409, "a refused send is reported as a conflict, not as success");
    }

    // 17. Notifications: the app's own history, read state and preferences over the API.
    {
        Assert(api.Get("/api/notifications", token).StatusCode == 503,
            "without notification services the endpoint says unavailable");

        var notificationSettings = new FakeNotificationSettings();
        var notificationCenter = new MyAiAssistant.Services.Notifications.NotificationCenter(
            notificationSettings, Path.Combine(directory, "notifications"));
        var notifApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            notificationSettings, notificationCenter);

        Assert(notifApi.Get("/api/notifications", null).StatusCode == 401, "notification state needs the token");

        notificationCenter.Add(MyAiAssistant.Services.Notifications.NotificationKind.ApprovalPending,
            "需要你的确认", "写入 D:\\Dev\\a.txt", "t1", "i1",
            MyAiAssistant.Services.Notifications.NotificationSeverity.Warning);

        var state = notifApi.Get("/api/notifications", token);
        Assert(state.StatusCode == 200, "notification state is readable with the token");
        using (var document = JsonDocument.Parse(state.Body))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("unread").GetInt32() == 1, "the unread count is reported");
            Assert(root.GetProperty("items").GetArrayLength() == 1, "the history is listed");

            var entry = root.GetProperty("items")[0];
            Assert(entry.GetProperty("kind").GetString() == "ApprovalPending", "the kind is reported");
            Assert(entry.GetProperty("unread").GetBoolean(), "and it starts unread");
            Assert(entry.GetProperty("actions").GetArrayLength() == 0, "no actions were attached to this one");
        }

        var id = notificationCenter.Recent()[0].Id;
        Assert((await notifApi.PostAsync("/api/notifications/read", null, "{}")).StatusCode == 401,
            "marking read requires the token");
        Assert((await notifApi.PostAsync("/api/notifications/read", token, "{}")).StatusCode == 400,
            "marking read without an id is a client error");
        Assert((await notifApi.PostAsync("/api/notifications/read", token, "{\"id\":\"n-nope\"}")).StatusCode == 404,
            "an unknown notification is a 404, not a silent success");
        Assert((await notifApi.PostAsync("/api/notifications/read", token, "{\"id\":\"" + id + "\"}")).StatusCode == 200,
            "marking read works");
        Assert(notificationCenter.UnreadCount == 0, "and it reaches the history");

        Assert((await notifApi.PostAsync("/api/notifications/read-all", token, null)).StatusCode == 200,
            "mark all read is accepted");
        Assert((await notifApi.PostAsync("/api/notifications/clear", token, null)).StatusCode == 200,
            "clearing the history is accepted");
        Assert(notificationCenter.Recent().Count == 0, "the history is empty afterwards");

        Assert((await notifApi.PostAsync("/api/notifications/settings", token, "{}")).StatusCode == 400,
            "an empty settings update is a client error");
        Assert((await notifApi.PostAsync("/api/notifications/settings", token, "not json")).StatusCode == 400,
            "malformed JSON is refused");
        Assert((await notifApi.PostAsync("/api/notifications/settings", null, "{\"enabled\":false}")).StatusCode == 401,
            "changing preferences requires the token");

        var applied = await notifApi.PostAsync("/api/notifications/settings", token,
            "{\"enabled\":false,\"taskCompleted\":false}");
        Assert(applied.StatusCode == 200, $"a preference change is accepted: {applied.Body}");
        Assert(!notificationSettings.Current.Enabled && !notificationSettings.Current.TaskCompleted,
            "the change reaches the settings store");
    }

    // Home Assistant: off until wired, token-gated once wired, and the view never carries a token.
    {
        var disabledApi = new DashboardApi(center, audit, token, store);
        Assert(disabledApi.Get("/api/homeassistant", token).StatusCode == 503,
            "without a home assistant client the endpoint says unavailable");
        Assert((await disabledApi.PostAsync("/api/homeassistant/connect", token, "{}")).StatusCode == 503,
            "home assistant actions are unavailable too");

        var ha = new DashboardTests.FakeHomeAssistantControl();
        var haApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null, null, null, ha);

        Assert(haApi.Get("/api/homeassistant", null).StatusCode == 401, "home assistant state needs the token");

        var read = haApi.Get("/api/homeassistant", token);
        Assert(read.StatusCode == 200, "home assistant state is readable with the token");
        using (var document = JsonDocument.Parse(read.Body))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("connected").GetBoolean(), "the connection state is reported");
            Assert(root.GetProperty("entities")[0].GetProperty("entityId").GetString() == "light.kitchen",
                "entities are listed");
            Assert(root.GetProperty("scenes").GetArrayLength() == 1, "scenes are listed");
            Assert(!read.Body.Contains("accessToken", StringComparison.Ordinal), "the view never carries a token");
            Assert(!read.Body.Contains("refreshToken", StringComparison.Ordinal), "and no refresh token either");
        }

        var settingsResult = await haApi.PostAsync("/api/homeassistant/settings", token,
            "{\"serverUrl\":\"192.168.1.10:8123\"}");
        Assert(settingsResult.StatusCode == 200 && ha.LastSettings == "192.168.1.10:8123",
            "the address reaches the control");
        Assert((await haApi.PostAsync("/api/homeassistant/settings", token, "{\"serverUrl\":\"ftp://x\"}")).StatusCode == 409,
            "a non-http address is refused rather than silently dropped");

        var begin = await haApi.PostAsync("/api/homeassistant/authorize", token, "{}");
        using (var document = JsonDocument.Parse(begin.Body))
            Assert(document.RootElement.GetProperty("authorizeUrl").GetString()!.Contains("auth/authorize"),
                "the authorize link is handed to the page");

        var callResult = await haApi.PostAsync("/api/homeassistant/call", token,
            "{\"domain\":\"light\",\"service\":\"turn_on\",\"entityId\":\"light.kitchen\"}");
        Assert(callResult.StatusCode == 200 && ha.LastCall == "light.turn_on|light.kitchen",
            "a service call reaches the control");
        Assert((await haApi.PostAsync("/api/homeassistant/call", null, "{}")).StatusCode == 401,
            "service calls require the token");

        Assert((await haApi.PostAsync("/api/homeassistant/signout", token, "{}")).StatusCode == 200 && ha.SignedOut,
            "signing out reaches the control");
    }

    // Task-summary sync: off until wired, token-gated once wired, and revoking is a real endpoint.
    {
        var disabledApi = new DashboardApi(center, audit, token, store);
        Assert(disabledApi.Get("/api/summary-sync", token).StatusCode == 503,
            "without a summary-sync control the endpoint says unavailable");

        var summary = new DashboardTests.FakeSummarySyncControl();
        var summaryApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            null, null, null, null, summary);

        Assert(summaryApi.Get("/api/summary-sync", null).StatusCode == 401,
            "summary-sync state needs the token");

        var state = summaryApi.Get("/api/summary-sync", token);
        Assert(state.StatusCode == 200, "summary-sync state is readable with the token");
        using (var document = JsonDocument.Parse(state.Body))
        {
            var root = document.RootElement;
            Assert(!root.GetProperty("enabled").GetBoolean(), "it starts disabled");
            Assert(root.GetProperty("pairedDeviceCount").GetInt32() == 1, "paired devices are counted");
            Assert(root.GetProperty("devices")[0].GetProperty("deviceName").GetString() == "Pixel 8",
                "the device is listed");
        }

        Assert((await summaryApi.PostAsync("/api/summary-sync/settings", null, "{\"enabled\":true}")).StatusCode == 401,
            "changing the switch requires the token");
        Assert((await summaryApi.PostAsync("/api/summary-sync/settings", token, "{}")).StatusCode == 400,
            "a body without enabled is a client error");
        Assert((await summaryApi.PostAsync("/api/summary-sync/settings", token, "not json")).StatusCode == 400,
            "malformed JSON is refused");

        var applied = await summaryApi.PostAsync("/api/summary-sync/settings", token, "{\"enabled\":true}");
        Assert(applied.StatusCode == 200 && summary.LastApplied == true, "the switch reaches the control");

        var pairControl = await summaryApi.PostAsync("/api/summary-sync/pair-control", token, "{\"enabled\":true}");
        Assert(pairControl.StatusCode == 200 && summary.LastAppliedPairControl == true,
            "the paired-control switch is separate and reaches the control");
        Assert((await summaryApi.PostAsync("/api/summary-sync/pair-control", token, "{}")).StatusCode == 400,
            "the paired-control switch needs an explicit value");
        using (var document = JsonDocument.Parse(summaryApi.Get("/api/summary-sync", token).Body))
            Assert(document.RootElement.GetProperty("pairControlEnabled").GetBoolean(),
                "the state reports the paired-control switch");

        var revoked = await summaryApi.PostAsync("/api/summary-sync/devices/Pixel%208/revoke", token, null);
        Assert(revoked.StatusCode == 200 && summary.LastRevoked == "Pixel 8",
            "revoking a device reaches the control, with the name unescaped");

        var unknown = await summaryApi.PostAsync("/api/summary-sync/devices/Nope/revoke", token, null);
        Assert(unknown.StatusCode == 409, "revoking an unknown device is a conflict, not a silent success");
    }

    // Screen session: the pause / resume / stop buttons, and stop really means stop.
    {
        var disabledApi = new DashboardApi(center, audit, token, store);
        Assert(disabledApi.Get("/api/screen", token).StatusCode == 503,
            "without a screen session the endpoint says unavailable");

        var screen = new DashboardTests.FakeScreenSessionControl();
        var screenApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            null, null, null, null, null, screen);

        Assert(screenApi.Get("/api/screen", null).StatusCode == 401, "screen state needs the token");

        var state = screenApi.Get("/api/screen", token);
        Assert(state.StatusCode == 200, "screen state is readable with the token");
        using (var document = JsonDocument.Parse(state.Body))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("state").GetString() == "Running", "a fresh session runs");
            Assert(root.GetProperty("supported").GetBoolean(), "the platform verdict is reported");
            Assert(root.GetProperty("platform").GetString() == "windows", "the platform is named");
        }

        Assert((await screenApi.PostAsync("/api/screen/pause", null, null)).StatusCode == 401,
            "pausing requires the token");

        var paused = await screenApi.PostAsync("/api/screen/pause", token, null);
        Assert(paused.StatusCode == 200, "pausing is accepted");
        using (var document = JsonDocument.Parse(screenApi.Get("/api/screen", token).Body))
            Assert(document.RootElement.GetProperty("state").GetString() == "Paused", "the state really changed");

        Assert((await screenApi.PostAsync("/api/screen/resume", token, null)).StatusCode == 200,
            "resuming is accepted");
        Assert((await screenApi.PostAsync("/api/screen/stop", token, null)).StatusCode == 200, "stopping works");

        var stopped = await screenApi.PostAsync("/api/screen/resume", token, null);
        Assert(stopped.StatusCode == 409, "a stopped session refuses to resume, and says so");
        using (var document = JsonDocument.Parse(screenApi.Get("/api/screen", token).Body))
            Assert(document.RootElement.GetProperty("state").GetString() == "Stopped", "it stays stopped");

        var unsupported = new DashboardTests.FakeScreenSessionControl { Supported = false };
        var unsupportedApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            null, null, null, null, null, unsupported);
        using (var document = JsonDocument.Parse(unsupportedApi.Get("/api/screen", token).Body))
        {
            Assert(!document.RootElement.GetProperty("supported").GetBoolean(), "an unsupported platform is flagged");
            Assert(document.RootElement.GetProperty("unsupportedReason").GetString()!.Contains("验证", StringComparison.Ordinal),
                "and the reason is shown");
        }
    }

    // MCP backend: off until wired, and starting/stopping goes through the control.
    {
        var disabledApi = new DashboardApi(center, audit, token, store);
        Assert(disabledApi.Get("/api/mcp", token).StatusCode == 503,
            "without an MCP backend the endpoint says unavailable");

        var mcp = new DashboardTests.FakeMcpStatus();
        var mcpApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            null, null, null, null, null, null, mcp);

        Assert(mcpApi.Get("/api/mcp", null).StatusCode == 401, "MCP state needs the token");

        using (var document = JsonDocument.Parse(mcpApi.Get("/api/mcp", token).Body))
            Assert(!document.RootElement.GetProperty("connected").GetBoolean(), "it starts disconnected");

        Assert((await mcpApi.PostAsync("/api/mcp/start", null, null)).StatusCode == 401,
            "starting the server requires the token");

        var started = await mcpApi.PostAsync("/api/mcp/start", token, null);
        Assert(started.StatusCode == 200, "starting is accepted");
        using (var document = JsonDocument.Parse(mcpApi.Get("/api/mcp", token).Body))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("connected").GetBoolean(), "the state reports connected");
            Assert(root.GetProperty("toolNames")[0].GetString() == "click", "published tools are listed");
        }

        Assert((await mcpApi.PostAsync("/api/mcp/stop", token, null)).StatusCode == 200, "stopping is accepted");
        using (var document = JsonDocument.Parse(mcpApi.Get("/api/mcp", token).Body))
            Assert(!document.RootElement.GetProperty("connected").GetBoolean(), "and the tools are gone");
    }

    // --- WebChat control surface ------------------------------------------
    {
        // Without a control the feature reports itself unavailable instead of pretending.
        var withoutWebChat = new DashboardApi(center, audit, token, store);
        Assert(withoutWebChat.Get("/api/webchat", token).StatusCode == 503, "webchat state says unavailable");
        Assert((await withoutWebChat.PostAsync("/api/webchat/start", token, null)).StatusCode == 503,
            "webchat start says unavailable");

        var webChat = new DashboardTests.FakeWebChatControl();
        var webChatApi = new DashboardApi(center, audit, token, store, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, webChat);

        Assert(webChatApi.Get("/api/webchat", null).StatusCode == 401, "webchat state needs the token");
        Assert((await webChatApi.PostAsync("/api/webchat/start", null, null)).StatusCode == 401,
            "webchat start needs the token");

        using (var document = JsonDocument.Parse(webChatApi.Get("/api/webchat", token).Body))
        {
            var root = document.RootElement;
            Assert(!root.GetProperty("running").GetBoolean(), "it starts stopped");
            Assert(root.GetProperty("port").GetInt32() == 9881, "the default port is reported");
            Assert(root.GetProperty("url").GetString() == "ws://127.0.0.1:9881/", "the url is reported");
        }

        var start = await webChatApi.PostAsync("/api/webchat/start", token, null);
        Assert(start.StatusCode == 200, "starting is accepted");
        Assert(webChat.StartCalls == 1, "the control really started");
        using (var document = JsonDocument.Parse(webChatApi.Get("/api/webchat", token).Body))
            Assert(document.RootElement.GetProperty("running").GetBoolean(), "the state now reports running");

        Assert((await webChatApi.PostAsync("/api/webchat/stop", token, null)).StatusCode == 200, "stopping is accepted");
        Assert(webChat.StopCalls == 1, "the control really stopped");
        using (var document = JsonDocument.Parse(webChatApi.Get("/api/webchat", token).Body))
            Assert(!document.RootElement.GetProperty("running").GetBoolean(), "and reports stopped again");

        // A refusal to start (port taken) must surface as an error, not as success.
        webChat.RefuseStart = true;
        Assert((await webChatApi.PostAsync("/api/webchat/start", token, null)).StatusCode == 400,
            "a refused start is an error");

        // Port selection: the only way out of "the default port is taken".
        webChat.RefuseStart = false;
        var withPort = await webChatApi.PostAsync("/api/webchat/start", token, "{\"port\":9955}");
        Assert(withPort.StatusCode == 200 && webChat.LastRequestedPort == 9955,
            "an explicit port reaches the control");
        using (var document = JsonDocument.Parse(webChatApi.Get("/api/webchat", token).Body))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("port").GetInt32() == 9955, "the state reports the chosen port");
            Assert(root.GetProperty("url").GetString() == "ws://127.0.0.1:9955/", "the url follows the port");
        }

        Assert((await webChatApi.PostAsync("/api/webchat/start", token, "{\"port\":0}")).StatusCode == 400,
            "port 0 is refused rather than treated as auto-assign");
        Assert((await webChatApi.PostAsync("/api/webchat/start", token, "{\"port\":70000}")).StatusCode == 400,
            "an out-of-range port is a client error");
        Assert((await webChatApi.PostAsync("/api/webchat/start", token, "{\"port\":\"x\"}")).StatusCode == 400,
            "a non-numeric port is a client error");
        Assert((await webChatApi.PostAsync("/api/webchat/start", token, "not json")).StatusCode == 400,
            "a malformed body is a client error");

        await webChatApi.PostAsync("/api/webchat/stop", token, null);
        Assert(webChat.LastRequestedPort == 9955, "stopping does not forget the chosen port");
    }

    // --- 控制中心页面与它的脚本必须对得上 ----------------------------------
    {
        var page = WebDashboardService.Page;

        Assert(page.Contains("id=\"wcStartBtn\"", StringComparison.Ordinal), "the chat card ships a start button");
        Assert(page.Contains("id=\"wcStopBtn\"", StringComparison.Ordinal), "the chat card ships a stop button");
        Assert(page.Contains("controlWebChat('start')", StringComparison.Ordinal), "the start button calls start");
        Assert(page.Contains("controlWebChat('stop')", StringComparison.Ordinal), "the stop button calls stop");
        Assert(page.Contains("/api/webchat/", StringComparison.Ordinal), "the card talks to the webchat endpoint");
        Assert(page.Contains("loadWebChat();", StringComparison.Ordinal), "the card refreshes with the rest of the page");

        // Every element the script reaches for must exist in the markup: a handler bound to a
        // missing id throws at load and the page quietly stops working past that point.
        var referenced = System.Text.RegularExpressions.Regex
            .Matches(page, @"\$\('([A-Za-z0-9_]+)'\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();
        var missing = referenced.Where(id => !page.Contains($"id=\"{id}\"", StringComparison.Ordinal)).ToArray();
        Assert(missing.Length == 0, "every element the script touches exists: " + string.Join(",", missing));
    }

    // --- 对话页：结构与真实服务 ------------------------------------------
    {
        var chat = MyAiAssistant.Services.Dashboard.WebChatPage.Html;

        Assert(chat.Contains("ws://127.0.0.1:", StringComparison.Ordinal), "the page connects to the local socket");
        Assert(chat.Contains("type:'message'", StringComparison.Ordinal), "it speaks the message frame");
        Assert(chat.Contains("frame.type === 'delta'", StringComparison.Ordinal), "it renders streamed deltas");
        Assert(chat.Contains("frame.type === 'done'", StringComparison.Ordinal), "it finishes a turn on done");
        Assert(chat.Contains("frame.type === 'error'", StringComparison.Ordinal), "it reports errors, not silence");
        Assert(chat.Contains("URLSearchParams(location.search).get('port')", StringComparison.Ordinal),
            "the page follows the port it was opened with");

        var referenced = System.Text.RegularExpressions.Regex
            .Matches(chat, @"\$\('([A-Za-z0-9_]+)'\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();
        var missing = referenced.Where(id => !chat.Contains($"id=\"{id}\"", StringComparison.Ordinal)).ToArray();
        Assert(missing.Length == 0, "every element the chat script touches exists: " + string.Join(",", missing));

        // Serve it for real: a page that is only a constant string is not a page until it is served.
        var port = 18000 + Random.Shared.Next(0, 2000);
        var dashboard = new WebDashboardService(center, audit, store, port: port);
        Assert(await dashboard.StartAsync(), "the dashboard binds a free port");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync(dashboard.Url + "chat");
            var body = await response.Content.ReadAsStringAsync();
            Assert(response.StatusCode == System.Net.HttpStatusCode.OK, "GET /chat returns 200");
            Assert(body.Contains("仙灵 · 本机对话", StringComparison.Ordinal), "the chat page is what comes back");
            Assert(response.Content.Headers.ContentType?.MediaType == "text/html", "it is served as html");

            // The control centre opens /chat?port=<the port it started on>. A query string must not
            // stop the page from being served — the transport routes on the path only.
            var withQuery = await http.GetAsync(dashboard.Url + "chat?port=9877");
            Assert(withQuery.StatusCode == System.Net.HttpStatusCode.OK,
                "a query string does not break routing");
            Assert((await withQuery.Content.ReadAsStringAsync()).Contains("仙灵 · 本机对话", StringComparison.Ordinal),
                "the same page comes back with a query string attached");

            // The control centre page must offer a way in.
            var index = await http.GetAsync(dashboard.Url.TrimEnd('/'));
            var indexBody = await index.Content.ReadAsStringAsync();
            Assert(indexBody.Contains("wcOpenBtn", StringComparison.Ordinal), "the control centre links to the chat page");
            Assert(indexBody.Contains("/chat?port=", StringComparison.Ordinal),
                "the link carries the port the server was started on");
        }
        finally
        {
            dashboard.Stop();
        }
    }

    // --- 消息平台账号：添加、启用/停用、删除 --------------------------------
    {
        var config = new AppConfig();
        var accounts = new MessagingAccountStore(() => config, saved => config = saved);
        var accountsApi = new DashboardApi(center, audit, token, store, accounts);

        Assert(accountsApi.Get("/api/accounts", null).StatusCode == 401, "the account list needs the token");

        var added = accountsApi.Post("/api/accounts", token,
            "{\"platform\":\"WebChat\",\"displayName\":\"本机网页\",\"credentials\":{}}");
        Assert(added.StatusCode == 200, "an account can be added");
        var id = accounts.List().Single().Id;

        using (var document = JsonDocument.Parse(accountsApi.Get("/api/accounts", token).Body))
        {
            var first = document.RootElement.GetProperty("accounts")[0];
            Assert(first.GetProperty("displayName").GetString() == "本机网页", "the new account is listed");
            Assert(!first.GetProperty("enabled").GetBoolean(), "a new account starts disabled");
        }

        // Enabling is what lets a capability run at launch, and until this endpoint existed the
        // control centre had no way to reach that state at all.
        Assert((await accountsApi.PostAsync($"/api/accounts/{id}/enable", null, null)).StatusCode == 401,
            "enabling requires the token");

        var enabled = await accountsApi.PostAsync($"/api/accounts/{id}/enable", token, null);
        Assert(enabled.StatusCode == 200, "enabling is accepted");
        Assert(accounts.Find(id)!.Enabled, "the stored account is really enabled");
        using (var document = JsonDocument.Parse(accountsApi.Get("/api/accounts", token).Body))
            Assert(document.RootElement.GetProperty("accounts")[0].GetProperty("enabled").GetBoolean(),
                "the list reports it enabled");

        // Idempotent: a refreshed page re-sending the same intent must not see a conflict.
        var again = await accountsApi.PostAsync($"/api/accounts/{id}/enable", token, null);
        Assert(again.StatusCode == 200, "enabling twice is not an error");
        using (var document = JsonDocument.Parse(again.Body))
            Assert(!document.RootElement.GetProperty("changed").GetBoolean(), "the second call reports no change");

        Assert((await accountsApi.PostAsync($"/api/accounts/{id}/disable", token, null)).StatusCode == 200,
            "disabling is accepted");
        Assert(!accounts.Find(id)!.Enabled, "the stored account is really disabled");

        Assert((await accountsApi.PostAsync("/api/accounts/missing/enable", token, null)).StatusCode == 400,
            "enabling an unknown account is a client error, not a silent success");

        Assert((await accountsApi.PostAsync($"/api/accounts/{id}/remove", token, null)).StatusCode == 200,
            "removing is accepted");
        Assert(accounts.List().Count == 0, "the account is gone");
    }

    // --- 配置落盘：密钥不能被就地改成密文，也不能重复加密 -----------------
    {
        var config = new AppConfig();
        config.LLM.Provider = "Kimi";
        config.LLM.ApiKey = "sk-plain-roundtrip";

        ConfigManager.Save(config);

        // Saving is a side effect on the file, not on the caller's object: the app keeps using
        // that object to talk to providers, and a ciphertext there would be sent as the key.
        Assert(config.LLM.ApiKey == "sk-plain-roundtrip", "saving leaves the caller's key alone");

        var storedPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var stored = File.ReadAllText(storedPath);
        Assert(!stored.Contains("sk-plain-roundtrip", StringComparison.Ordinal),
            "the stored file does not contain the plaintext key");
        Assert(stored.Contains("Kimi", StringComparison.Ordinal), "the rest of the config is written as-is");

        var loaded = ConfigManager.Load();
        Assert(loaded.LLM.ApiKey == "sk-plain-roundtrip", "loading decrypts the key back");

        // Each Save protects again. If it protected the caller's object, a second save would
        // encrypt the ciphertext and the key would be unrecoverable.
        ConfigManager.Save(loaded);
        ConfigManager.Save(loaded);
        Assert(ConfigManager.Load().LLM.ApiKey == "sk-plain-roundtrip", "saving twice does not double-encrypt");
    }

    Console.WriteLine($"All {checks} dashboard checks passed.");
}
finally
{
    try { Directory.Delete(directory, true); } catch (IOException) { }
}
sealed class FakeSettingsStore : ISettingsStore
{
    public int Updates { get; private set; }
    public SettingsUpdate? Last { get; private set; }

    public SettingsSnapshot Read() => new(
        new EndpointSettings("Kimi", "https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6", null, true),
        new EndpointSettings("", "", "", null, false),
        new EndpointSettings("MiMo", "https://api.xiaomimimo.com/v1/audio/transcriptions", "", null, false),
        new EndpointSettings("MiMo", "https://api.xiaomimimo.com/v1/audio/speech", "mi-tts-v2.5", "茉莉", true),
        new EndpointSettings("OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", null, false),
        "Fairy", "你是 Fairy", "Fairy");

    public void Update(SettingsUpdate update)
    {
        Updates++;
        Last = update;
    }
}

internal sealed class FakeLocalSendPeers : ILocalSendPeers
{
    public bool DiscoveryRunning => true;

    public IReadOnlyList<LocalSendPeer> Peers() =>
    [
        new("fp-1", "Pixel", "mobile", "http://192.168.1.9:53317", DateTimeOffset.UtcNow)
    ];

    public Task<LocalSendSendResult> SendAsync(
        string fingerprint, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Task.FromResult(fingerprint == "fp-1"
            ? new LocalSendSendResult(true, $"已发送 {paths.Count} 个文件", paths.Count)
            : new LocalSendSendResult(false, "目标设备已不在线（对方停止广播）"));
}

internal sealed class FakeLocalSendSettings : ILocalSendSettings
{
    public (bool? Enabled, string? Alias, string? SaveDirectory)? Applied { get; private set; }

    public LocalSendSettings Read() =>
        new(Applied?.Enabled == true, false, 53317, "仙灵",
            Path.Combine(Path.GetTempPath(), "fairyai-received"), 2L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024);

    public (bool Ok, string Message) Apply(bool? enabled, string? alias, string? saveDirectory)
    {
        if (alias is { Length: > 40 }) return (false, "设备名过长（最多 40 个字符）");
        Applied = (enabled, alias, saveDirectory);
        return (true, enabled == true ? "接收已开启" : "已保存设置");
    }
}

internal sealed class StubModels : MyAiAssistant.Services.FairyCode.IFairyCodeModelSource
{
    public (EndpointSettings Endpoint, string? ApiKey)? Read() =>
        (new EndpointSettings("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", null, true), "sk-test");
}

internal sealed class StubEngine : MyAiAssistant.Services.FairyCode.IEngineLocator
{
    public Task<MyAiAssistant.Services.FairyCode.EngineAvailability> ProbeAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MyAiAssistant.Services.FairyCode.EngineAvailability.Unusable(
            MyAiAssistant.Services.FairyCode.EngineAvailabilityState.CliMissing,
            "未找到 Pi Agent CLI：测试环境不提供引擎"));
}


internal sealed class FakePairingControl : IPairingControl
{
    public (bool? Enabled, int? Port, string? DisplayName)? Applied { get; private set; }

    public string? CreatedGroup { get; private set; }

    public (string GroupId, string DeviceId, string Capability)? LastGrant { get; private set; }

    public (string GroupId, string DeviceId, string Capability)? LastWithdraw { get; private set; }

    public (string GroupId, string DeviceId)? LastRemoved { get; private set; }

    public PairingView Read() => new(
        Applied?.Enabled == true,
        false,
        9879,
        "测试机",
        "DEVFAKE0001",
        "DEVF-AKE0-001",
        "GRPHOME",
        null,
        null,
        [
            new DeviceGroup("GRPHOME", "家用", DateTimeOffset.UtcNow, 2, "AAAA-BBBB-CCCC",
            [
                new GroupMember("DEVPIXEL", "手机", "signing-key", "agreement-key", DateTimeOffset.UtcNow,
                    Array.Empty<PairingCapability>())
            ])
        ],
        [new PairingInviteInfo("INV1", "GRPHOME", DateTimeOffset.UtcNow.AddMinutes(9), 5)]);

    public PairingActionResult Apply(bool? enabled, int? port, string? displayName)
    {
        if (port is < 1024) return new PairingActionResult(false, "端口需在 1024 到 65535 之间");
        Applied = (enabled, port, displayName);
        return new PairingActionResult(true, enabled == true ? "配对已开启" : "配对已关闭");
    }

    public PairingActionResult CreateGroup(string name)
    {
        CreatedGroup = name;
        return new PairingActionResult(true, "已创建设备组");
    }

    public PairingActionResult SetActiveGroup(string groupId) =>
        new(groupId == "GRPHOME", groupId == "GRPHOME" ? "已切换" : "设备组不存在");

    public PairingActionResult CreateInvite(string? groupId) => groupId == "GRPHOME"
        ? new PairingActionResult(true, "已生成配对码", "123456")
        : new PairingActionResult(false, "设备组不存在");

    public PairingActionResult CancelInvite(string inviteId) => new(true, "邀请已作废");

    public PairingActionResult Grant(string groupId, string deviceId, string capability)
    {
        if (!Enum.TryParse<PairingCapability>(capability, out var parsed) || !Enum.IsDefined(parsed))
            return new PairingActionResult(false, "未知的能力");

        LastGrant = (groupId, deviceId, capability);
        return new PairingActionResult(true, "已授予");
    }

    public PairingActionResult Withdraw(string groupId, string deviceId, string capability)
    {
        if (!Enum.TryParse<PairingCapability>(capability, out var parsed) || !Enum.IsDefined(parsed))
            return new PairingActionResult(false, "未知的能力");

        LastWithdraw = (groupId, deviceId, capability);
        return new PairingActionResult(true, "已收回");
    }

    public PairingActionResult RemoveMember(string groupId, string deviceId)
    {
        LastRemoved = (groupId, deviceId);
        return new PairingActionResult(true, "已移除并更换组密钥");
    }

    public PairingActionResult RotateKey(string groupId) => new(true, "组密钥已更换");
}
internal sealed class FakeTransferControl : ITransferControl
{
    public (string DeviceId, int PathCount)? LastSend { get; private set; }

    public TransferView Read() => new(
        false,
        null,
        "DEVHOST0001",
        @"D:\received",
        [new TransferPeer("DEVPIXEL", "平板", "GRPHOME", "家用")],
        [new TransferJob("t0001", "DEVPIXEL", "接收", "已完成", 1024, null, DateTimeOffset.UtcNow)],
        [new ReceivedFileView("a.png", 1024, true, @"D:\received\a.png")]);

    public Task<TransferCommandResult> SendAsync(string deviceId, IReadOnlyList<string> paths)
    {
        if (deviceId == "GONE") return Task.FromResult(new TransferCommandResult(false, "这台设备不在任何已配对的设备组里"));

        LastSend = (deviceId, paths.Count);
        return Task.FromResult(new TransferCommandResult(true, $"已发送 {paths.Count} 个文件"));
    }
}
internal sealed class FakeNotificationSettings : MyAiAssistant.Services.Notifications.INotificationSettings
{
    public MyAiAssistant.Services.Notifications.NotificationPreferences Current { get; private set; } =
        MyAiAssistant.Services.Notifications.NotificationPreferences.Defaults;

    public MyAiAssistant.Services.Notifications.NotificationPreferences Read() => Current;

    public (bool Ok, string Message) Apply(MyAiAssistant.Services.Notifications.NotificationPreferences preferences)
    {
        Current = preferences;
        return (true, "已保存通知设置");
    }
}