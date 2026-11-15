using System.Net.Http.Json;
using System.Text.Json;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Dashboard;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.Permissions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var tempRoot = Path.Combine(Path.GetTempPath(), "fairyai-fairycode-" + Guid.NewGuid().ToString("N"));
var workspace = Path.Combine(tempRoot, "workspace");
Directory.CreateDirectory(workspace);
var existingFile = Path.Combine(workspace, "existing.txt");
File.WriteAllText(existingFile, "hello");

try
{
    // ---- 1. The classifier: what needs the user and what does not ----
    {
        var classifier = new FairyToolClassifier(workspace);

        var readExisting = classifier.Classify(new EngineToolCall("read", new Dictionary<string, string>
        {
            ["path"] = existingFile
        }));
        Assert(readExisting.IsReadOnly && readExisting.Request.Category == RiskCategory.None,
            "reading a normal file is not a risk category");

        var readSecret = classifier.Classify(new EngineToolCall("read", new Dictionary<string, string>
        {
            ["path"] = Path.Combine(workspace, ".env")
        }));
        Assert(readSecret.Request.Category == RiskCategory.ReadWriteCredentials,
            "reading a credential-shaped path needs approval");

        var newFile = classifier.Classify(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = Path.Combine(workspace, "brand-new.txt")
        }));
        Assert(newFile.Request.Category == RiskCategory.None, "creating a new file inside the workspace is additive");

        var overwrite = classifier.Classify(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = existingFile
        }));
        Assert(overwrite.Request.Category == RiskCategory.DeleteOrOverwriteData,
            "overwriting an existing file needs approval");

        var outside = classifier.Classify(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = Path.Combine(tempRoot, "outside.txt")
        }));
        Assert(outside.Request.Category == RiskCategory.DeleteOrOverwriteData,
            "writing outside the workspace needs approval");

        var unknownTool = classifier.Classify(new EngineToolCall("shell_exec_whatever", new Dictionary<string, string>()));
        Assert(unknownTool.Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "an unregistered tool fails closed as unverified code");

        // Read-only commands stay usable; anything that can chain or write does not.
        Assert(classifier.Classify(Command("ls -la")).Request.Category == RiskCategory.None, "listing is read-only");
        Assert(classifier.Classify(Command("git status")).Request.Category == RiskCategory.None, "git status is read-only");
        Assert(classifier.Classify(Command("git log --oneline -5")).Request.Category == RiskCategory.None, "git log is read-only");
        Assert(classifier.Classify(Command("rm -rf build")).Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "removal needs approval");
        Assert(classifier.Classify(Command("ls && rm -rf build")).Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "command chaining is never read-only");
        Assert(classifier.Classify(Command("git push origin main")).Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "publishing needs approval");
        Assert(classifier.Classify(Command("find . -exec rm {} ;")).Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "find -exec is execution, not search");
        Assert(classifier.Classify(Command("cat ~/.ssh/id_rsa")).Request.Category == RiskCategory.ReadWriteCredentials,
            "reading a key through the shell needs approval");
        Assert(classifier.Classify(Command("echo hi > out.txt")).Request.Category == RiskCategory.InstallOrRunUnverifiedCode,
            "redirection is a write");
        Assert(classifier.Classify(Command("node --version")).Request.Category == RiskCategory.None, "a version probe is read-only");
        Assert(!FairyToolClassifier.IsReadOnlyCommand(""), "empty command is not read-only");
    }

    // ---- 2. The gate honours a task-scoped approver, and still fails closed ----
    {
        var auditDir = Path.Combine(tempRoot, "audit1");
        var audit = new OperationAudit(auditDir);
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var center = new AgentControlCenter();
        var task = center.Enqueue("approval test");
        task.Start();

        var request = new ActionRequest("test.delete", RiskCategory.DeleteOrOverwriteData, "删除文件", existingFile);

        // No approver supplied and none registered => denied, not allowed.
        var noApprover = await gate.AuthorizeAsync(request);
        Assert(!noApprover.IsAllowed && noApprover.Decision == "no-approver", "no approver is a denial");

        var approveTask = gate.AuthorizeAsync(request, CancellationToken.None, TaskApproval.For(task));
        await Task.Delay(200);
        Assert(center.Pending().Count == 1, "the gate surfaced a pending approval for the task");
        var interaction = center.Pending()[0];
        Assert(center.Answer(interaction.Id, approved: true), "the pending approval can be answered");
        var approved = await approveTask;
        Assert(approved.IsAllowed, "answering yes lets the action through");

        // Stopping the task while it waits must deny, not leave a zombie approval.
        var stopTask = gate.AuthorizeAsync(request, CancellationToken.None, TaskApproval.For(task));
        await Task.Delay(200);
        center.RequestStop(task.Id);
        var stopped = await stopTask;
        Assert(!stopped.IsAllowed, "stopping the task denies the waiting action");
        Assert(center.Pending().Count == 0, "the withdrawn approval leaves no orphan");
    }

    // ---- 3. Session: pause is enforced at the tool-call boundary ----
    {
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit2"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var center = new AgentControlCenter();
        var entity = center.Enqueue("paused session", "Pi Agent", "test/model");
        entity.Start();
        var session = new FairyCodeSession(
            entity.Id, entity.Title, workspace, "Pi Agent", "test/model", entity, gate, audit, _ => { });

        var classify = new EngineToolCall("bash", new Dictionary<string, string> { ["command"] = "rm -rf build" });

        center.RequestPause(entity.Id);
        var pendingDecision = session.DecideAsync(classify);
        await Task.Delay(300);
        Assert(!pendingDecision.IsCompleted, "a paused task does not start the next tool call");
        Assert(center.Pending().Count == 0, "a paused task has not asked for approval yet");

        center.RequestResume(entity.Id);
        await Task.Delay(200);
        Assert(center.Pending().Count == 1, "resuming lets the tool call reach the approval gate");
        center.Answer(center.Pending()[0].Id, approved: false);
        var denied = await pendingDecision;
        Assert(!denied.Allowed, "a denied approval blocks the tool call");

        // A stopped task refuses every further tool call outright.
        center.RequestStop(entity.Id);
        var afterStop = await session.DecideAsync(classify);
        Assert(!afterStop.Allowed, "a stopped task cannot run tool calls");
        session.Dispose();
    }

    // ---- 4. The bridge: token, loopback discipline, and fail-closed behaviour ----
    {
        var oracle = new FakeOracle((taskId, call, _) =>
            Task.FromResult(call.ToolName == "bash"
                ? (false, "需要用户确认")
                : (true, "只读操作")));
        var bridge = new FairyCodeBridgeServer(oracle);
        Assert(bridge.Start(), "the loopback bridge binds: " + bridge.LastError);
        try
        {
            using var http = new HttpClient();
            var url = bridge.Url! + "decide";

            var noToken = await http.SendAsync(JsonPost(url, new { taskId = "t1", toolName = "read", input = new { } }));
            Assert((int)noToken.StatusCode == 401, "a request without the token is rejected");

            var wrong = await http.SendAsync(JsonPost(url, new { taskId = "t1", toolName = "read", input = new { } }, "not-the-token"));
            Assert((int)wrong.StatusCode == 401, "a wrong token is rejected");

            var allowResponse = await http.SendAsync(
                JsonPost(url, new { taskId = "t1", toolName = "read", input = new { path = existingFile } }, bridge.Token));
            using var allowJson = JsonDocument.Parse(await allowResponse.Content.ReadAsStringAsync());
            Assert(allowJson.RootElement.GetProperty("allow").GetBoolean(), "an allowed call answers allow=true");

            var denyResponse = await http.SendAsync(
                JsonPost(url, new { taskId = "t1", toolName = "bash", input = new { command = "rm -rf build" } }, bridge.Token));
            using var denyJson = JsonDocument.Parse(await denyResponse.Content.ReadAsStringAsync());
            Assert(!denyJson.RootElement.GetProperty("allow").GetBoolean(), "a denied call answers allow=false");
            Assert(denyJson.RootElement.GetProperty("reason").GetString()!.Length > 0, "a denial carries a reason");

            var badBody = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json")
            };
            badBody.Headers.Add("X-Fairy-Bridge", bridge.Token);
            Assert((int)(await http.SendAsync(badBody)).StatusCode == 400, "a malformed body is a 400");

            var missingFields = await http.SendAsync(JsonPost(url, new { toolName = "read" }, bridge.Token));
            Assert((int)missingFields.StatusCode == 400, "a request without a task id is a 400");

            var wrongMethod = new HttpRequestMessage(HttpMethod.Get, bridge.Url! + "decide");
            wrongMethod.Headers.Add("X-Fairy-Bridge", bridge.Token);
            Assert((int)(await http.SendAsync(wrongMethod)).StatusCode == 405, "only POST is accepted");
        }
        finally
        {
            bridge.Stop();
        }

        // An oracle that throws must never turn into an approval.
        var throwing = new FairyCodeBridgeServer(new FakeOracle((_, _, _) => throw new InvalidOperationException("boom")));
        Assert(throwing.Start(), "second bridge binds");
        try
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(
                JsonPost(throwing.Url! + "decide", new { taskId = "t1", toolName = "read", input = new { } }, throwing.Token));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(!json.RootElement.GetProperty("allow").GetBoolean(), "an oracle failure fails closed");
        }
        finally
        {
            throwing.Stop();
        }
    }

    // ---- 5. Session manager: unknown workspace and missing engine are reported, not faked ----
    {
        var audit = new OperationAudit(Path.Combine(tempRoot, "audit3"));
        var gate = new ApprovalGate(new PermissionPolicy(), audit);
        var center = new AgentControlCenter();
        var extension = FindAsset("Assets", "Pi", "fairy-approval.ts");
        Assert(extension is not null, "the approval extension is present for the manager tests");
        var manager = new FairyCodeSessionManager(
            center, gate, audit, new FakeModels(), new UnavailableEngine(), extension);
        try
        {
            var missingWorkspace = await manager.StartAsync("do something", Path.Combine(tempRoot, "missing"));
            Assert(!missingWorkspace.Ok && missingWorkspace.Message.Contains("工作区"), "a missing workspace is refused");

            var rootRefusal = await manager.StartAsync("do something", Path.GetPathRoot(tempRoot));
            Assert(!rootRefusal.Ok && rootRefusal.Message.Contains("根目录"), "a drive root is refused as a workspace");

            var noEngine = await manager.StartAsync("do something", workspace);
            Assert(!noEngine.Ok, "a missing engine refuses the start");
            Assert(noEngine.Message.Contains("Node", StringComparison.Ordinal)
                   || noEngine.Message.Contains("Pi Agent CLI", StringComparison.Ordinal),
                "the refusal names what is missing");

            var unknown = await manager.DecideAsync("no-such-task",
                new EngineToolCall("bash", new Dictionary<string, string>()), CancellationToken.None);
            Assert(!unknown.Allowed, "the manager denies tool calls for unknown tasks");
        }
        finally
        {
            manager.Dispose();
        }
    }

    // ---- 6. Event parsing ----
    {
        Assert(PiRpcParser.Parse("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"hi\"}}").Kind
               == PiEventKind.Text, "text deltas are recognised");
        Assert(PiRpcParser.Parse("{\"type\":\"tool_execution_start\",\"toolName\":\"bash\"}").ToolName == "bash",
            "tool starts carry the tool name");
        Assert(PiRpcParser.Parse("{\"type\":\"agent_settled\"}").Kind == PiEventKind.Settled, "settling is recognised");
        Assert(PiRpcParser.Parse("{\"type\":\"extension_error\",\"error\":\"boom\"}").Kind == PiEventKind.Error,
            "extension errors are surfaced");
        Assert(PiRpcParser.Parse("not json").Kind == PiEventKind.Other, "a malformed line is ignored, not fatal");
        Assert(PiRpcParser.Parse("").Kind == PiEventKind.Other, "an empty line is ignored");
    }

    // ---- 7. Model binding ----
    {
        var canonical = PiModelBindingFactory.Create(
            new EndpointSettings("DeepSeek", "https://api.deepseek.com/v1/chat/completions", "deepseek-chat", null, true),
            "sk-test");
        Assert(canonical is not null, "a canonical provider produces a binding");
        Assert(canonical!.ProviderArgument == "deepseek", "the canonical provider id is used");
        Assert(canonical.Environment["DEEPSEEK_API_KEY"] == "sk-test", "the key travels in the environment");
        Assert(canonical.Environment.Values.Contains("sk-test"), "the key really reaches the child environment");

        var proxied = PiModelBindingFactory.Create(
            new EndpointSettings("DeepSeek", "https://proxy.example.com/v1", "deepseek-chat", null, true),
            "sk-test");
        Assert(proxied is not null && proxied.ProviderArgument == "fairy-endpoint",
            "a provider behind a different host becomes an explicit custom endpoint");
        Assert(proxied!.Environment["FAIRY_CUSTOM_BASE_URL"] == "https://proxy.example.com/v1", "custom base url is passed");

        var trimmed = PiModelBindingFactory.Create(
            new EndpointSettings("Kimi", "https://api.moonshot.cn/v1/chat/completions", "kimi-k2", null, true), "k");
        Assert(trimmed!.Environment["FAIRY_CUSTOM_BASE_URL"] == "https://api.moonshot.cn/v1",
            "a full completions URL is trimmed to the API root");

        Assert(PiModelBindingFactory.Create(
            new EndpointSettings("Whatever", "", "some-model", null, true), "k") is null,
            "an unknown provider without a base url is refused instead of guessing");
        Assert(PiModelBindingFactory.Create(
            new EndpointSettings("DeepSeek", "https://api.deepseek.com", "", null, true), "k") is null,
            "a missing model name is refused");
    }

    // ---- 8. Locator honesty ----
    {
        // Whatever it finds, a "ready" answer must point at files that really exist, and a
        // "not ready" answer must say which piece is missing.
        foreach (var locator in new[] { new PiAgentLocator(), new PiAgentLocator("Z:\\nope\\node.exe", "Z:\\nope\\cli.js") })
        {
            var located = locator.Locate();
            if (located.IsReady)
            {
                Assert(File.Exists(located.NodePath!), "a ready node path points at a real file");
                Assert(File.Exists(located.CliPath!), "a ready cli path points at a real file");
            }
            else
            {
                Assert(located.State != EngineAvailabilityState.Ready, "an unusable engine is not reported as ready");
                Assert(located.Detail.Length > 0, "the failure carries a human-readable reason");
            }
        }

        // Probe the real install when it is actually there; skip honestly when it is not.
        var real = await new PiAgentLocator().ProbeAsync();
        if (real.IsReady)
        {
            Assert(!string.IsNullOrWhiteSpace(real.Version), "the real Pi CLI reports a version");
        }
        else
        {
            Console.WriteLine($"  note: Pi CLI unavailable here ({real.State}: {real.Detail})");
        }
    }

    // ---- 9. The approval extension shipped with the app ----
    {
        var extension = FindAsset("Assets", "Pi", "fairy-approval.ts");
        Assert(extension is not null, "the pi approval extension ships with the app");
        var text = File.ReadAllText(extension!);
        Assert(text.Contains("tool_call", StringComparison.Ordinal), "the extension hooks tool_call");
        Assert(text.Contains("payload.allow === true", StringComparison.Ordinal), "it only allows an explicit true");
        Assert(text.Contains("return block(", StringComparison.Ordinal), "every other path blocks");
        Assert(text.Contains("FAIRY_BRIDGE_TOKEN", StringComparison.Ordinal), "it authenticates to the bridge");
        Assert(!text.Contains("process.exit", StringComparison.Ordinal), "it never exits the engine to dodge a block");
    }

    // ---- 10. Real engine launch: the extension loads and the RPC channel answers ----
    {        var locator = new PiAgentLocator();
        var availability = await locator.ProbeAsync();
        var extension = FindAsset("Assets", "Pi", "fairy-approval.ts");
        if (!availability.IsReady || extension is null)
        {
            Console.WriteLine($"  note: engine launch not exercised ({availability.State}: {availability.Detail})");
        }
        else
        {
            var oracle = new FakeOracle((_, _, _) => Task.FromResult((false, "测试环境一律拦截")));
            var bridge = new FairyCodeBridgeServer(oracle);
            Assert(bridge.Start(), "bridge for the launch test binds");
            var events = new List<PiEvent>();
            var binding = new PiModelBinding(null, null, new Dictionary<string, string>
            {
                // Exercises the custom-provider registration path in the extension.
                ["FAIRY_CUSTOM_BASE_URL"] = "https://example.invalid/v1",
                ["FAIRY_CUSTOM_MODEL"] = "demo-model",
                ["FAIRY_CUSTOM_KEY"] = "sk-not-a-real-key"
            }, "custom");
            var process = PiAgentProcess.Start(
                new PiLaunchOptions(availability.NodePath!, availability.CliPath!, extension, workspace,
                    "launch-test", bridge.Url!, bridge.Token, binding),
                piEvent => { lock (events) events.Add(piEvent); },
                line => { lock (events) events.Add(new PiEvent(PiEventKind.Other, Detail: "stderr: " + line)); });
            try
            {
                await process.SendCommandAsync("""{"type":"get_state"}""");
                var deadline = Environment.TickCount64 + 40_000;
                while (Environment.TickCount64 < deadline)
                {
                    lock (events)
                    {
                        if (events.Any(e => e.Kind == PiEventKind.Response)) break;
                    }
                    await Task.Delay(100);
                }

                lock (events)
                {
                    var errors = events.Where(e => e.Kind == PiEventKind.Error).Select(e => e.Detail).ToArray();
                    var diagnostics = string.Join(" | ", events
                        .Where(e => e.Kind == PiEventKind.Other && e.Detail is not null)
                        .Select(e => e.Detail).Take(6));
                    Assert(errors.Length == 0, "the extension loads without an error event: " + string.Join(" / ", errors));
                    Assert(events.Any(e => e.Kind == PiEventKind.Response),
                        "the engine answers an RPC command (no response seen in 40s). " + diagnostics);
                }
            }
            finally
            {
                process.Dispose();
                bridge.Stop();
            }
        }
    }

    // ---- 11. The vendored runtime: it ships with the app and it is verifiable ----
    {
        var vendored = new PiAgentLocator().FindCliWithSource();
        Assert(vendored.Path is not null, "a Pi Agent runtime is found on this machine");
        Assert(vendored.Source == EngineSource.Vendored,
            $"the runtime comes from the bundled copy, not a global install (got {vendored.Source})");
        Assert(vendored.Path!.EndsWith(Path.Combine("ThirdParty", "PiAgent", "dist", "bundle", "cli.js"), StringComparison.OrdinalIgnoreCase),
            "the bundled copy is the one under ThirdParty/PiAgent");

        var problems = PiAgentVendored.Verify(vendored.Path);
        Assert(problems.Count == 0, "the vendored copy matches its manifest: " + string.Join(" / ", problems));

        // A tampered copy must be caught, otherwise "verified runtime" is just a claim.
        var fakeRoot = Path.Combine(tempRoot, "fake-vendor");
        Directory.CreateDirectory(fakeRoot);
        Directory.CreateDirectory(Path.Combine(fakeRoot, "dist", "bundle"));
        File.WriteAllText(Path.Combine(fakeRoot, "dist", "bundle", "cli.js"), "console.log('tampered')");
        File.WriteAllText(Path.Combine(fakeRoot, PiAgentVendored.ManifestFile), """
            {
              "package": "@earendil-works/pi-coding-agent",
              "version": "0.84.3",
              "files": [ { "path": "dist/bundle/cli.js", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 } ]
            }
            """);
        var tampered = PiAgentVendored.Verify(Path.Combine(fakeRoot, "dist", "bundle", "cli.js"));
        Assert(tampered.Any(problem => problem.Contains("校验失败", StringComparison.Ordinal)),
            "a hash mismatch is reported instead of accepted");

        // A version drift is a failure too: the RPC contract is pinned to a version.
        var hashed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(fakeRoot, "dist", "bundle", "cli.js")))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(fakeRoot, PiAgentVendored.ManifestFile), $$"""
            {
              "package": "@earendil-works/pi-coding-agent",
              "version": "9.9.9",
              "files": [ { "path": "dist/bundle/cli.js", "sha256": "{{hashed}}", "bytes": 1 } ]
            }
            """);
        var drifted = PiAgentVendored.Verify(Path.Combine(fakeRoot, "dist", "bundle", "cli.js"));
        Assert(drifted.Any(problem => problem.Contains("版本", StringComparison.Ordinal)),
            "a vendored version that differs from the expected one is reported");

        File.Delete(Path.Combine(fakeRoot, PiAgentVendored.ManifestFile));
        Assert(PiAgentVendored.Verify(Path.Combine(fakeRoot, "dist", "bundle", "cli.js")).Count > 0,
            "a copy without a manifest cannot be verified");
    }

    // ---- 12. Change previews: the approval prompt has to show what will change ----
    {
        var target = Path.Combine(workspace, "preview.txt");
        File.WriteAllText(target, "one\ntwo\nthree\n");

        var modify = FairyDiffPreview.Build(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = target,
            ["content"] = "one\nTWO\nthree\n"
        }), workspace);
        Assert(modify is not null, "a write call gets a preview");
        Assert(modify!.Text.Contains("修改文件", StringComparison.Ordinal), "an existing file is previewed as a modification");
        Assert(modify.Text.Contains("-two", StringComparison.Ordinal) &&
               modify.Text.Contains("+TWO", StringComparison.Ordinal),
            "the preview shows both sides of the change");

        var created = FairyDiffPreview.Build(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = Path.Combine(workspace, "brand-new-preview.txt"),
            ["content"] = "alpha\nbeta\n"
        }), workspace);
        Assert(created is not null && created.Text.Contains("新建文件", StringComparison.Ordinal),
            "a new file is previewed as a new file");
        Assert(created!.Text.Contains("+alpha", StringComparison.Ordinal), "new content is shown as additions");

        var edits = FairyDiffPreview.Build(new EngineToolCall("edit", new Dictionary<string, string>
        {
            ["path"] = target,
            ["edits"] = """[{"oldText":"two","newText":"deux"}]"""
        }), workspace);
        Assert(edits is not null && edits.Text.Contains("+deux", StringComparison.Ordinal),
            "an edit call is applied before diffing");

        var mismatched = FairyDiffPreview.Build(new EngineToolCall("edit", new Dictionary<string, string>
        {
            ["path"] = target,
            ["edits"] = """[{"oldText":"not in the file","newText":"x"}]"""
        }), workspace);
        Assert(mismatched is not null && mismatched.Text.Contains("无法解析", StringComparison.Ordinal),
            "an edit that does not match the file produces no fake diff");

        Assert(FairyDiffPreview.Build(Command("ls"), workspace) is null,
            "a read-only call has no change preview");

        var big = string.Join('\n', Enumerable.Range(0, 4500).Select(i => i.ToString()));
        File.WriteAllText(Path.Combine(workspace, "big.txt"), big);
        var oversized = FairyDiffPreview.Build(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = Path.Combine(workspace, "big.txt"),
            ["content"] = big + "\nextra"
        }), workspace);
        Assert(oversized is not null && oversized.Text.Contains("文件过大", StringComparison.Ordinal),
            "an oversized file is summarised instead of diffed line by line");

        // The prompt the user answers really contains the preview, through the same gate.
        var previewAudit = new OperationAudit(Path.Combine(tempRoot, "audit-preview"));
        var previewGate = new ApprovalGate(new PermissionPolicy(), previewAudit);
        var previewCenter = new AgentControlCenter();
        var previewTask = previewCenter.Enqueue("preview session", "Pi Agent", "test/model");
        previewTask.Start();
        var previewSession = new FairyCodeSession(
            previewTask.Id, previewTask.Title, workspace, "Pi Agent", "test/model",
            previewTask, previewGate, previewAudit, _ => { });
        var decision = previewSession.DecideAsync(new EngineToolCall("write", new Dictionary<string, string>
        {
            ["path"] = target,
            ["content"] = "one\ntwo\nTHREE\n"
        }));
        await Task.Delay(300);
        var pendingPreview = previewCenter.Pending().Single();
        Assert(pendingPreview.Prompt.Contains("变更预览", StringComparison.Ordinal),
            "the pending approval carries the preview");
        Assert(pendingPreview.Prompt.Contains("+THREE", StringComparison.Ordinal), "the preview shows the new line");
        previewCenter.Answer(pendingPreview.Id, approved: false);
        var deniedPreview = await decision;
        Assert(!deniedPreview.Allowed, "denying the preview denies the write");
        previewSession.Dispose();
    }

    Console.WriteLine($"All {checks} Fairy Code checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{checks} checks passed, then: {ex.Message}");
    return 1;
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch (IOException) { }
}

static EngineToolCall Command(string command) =>
    new("bash", new Dictionary<string, string> { ["command"] = command });

static HttpRequestMessage JsonPost(string url, object payload, string? bridgeToken = null)
{
    var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
    };
    if (bridgeToken is not null) request.Headers.Add("X-Fairy-Bridge", bridgeToken);
    return request;
}

static string? FindAsset(params string[] parts)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

internal sealed class FakeOracle(
    Func<string, EngineToolCall, CancellationToken, Task<(bool, string)>> handler) : IToolOracle
{
    public Task<(bool Allowed, string Reason)> DecideAsync(
        string taskId, EngineToolCall call, CancellationToken cancellationToken) => handler(taskId, call, cancellationToken);
}

internal sealed class FakeModels : IFairyCodeModelSource
{
    public (EndpointSettings Endpoint, string? ApiKey)? Read() =>
        (new EndpointSettings("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", null, true), "sk-test");
}

internal sealed class UnavailableEngine : IEngineLocator
{
    public Task<EngineAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(EngineAvailability.Unusable(EngineAvailabilityState.CliMissing,
            "未找到 Pi Agent CLI（@earendil-works/pi-coding-agent）：请先安装该包，或用 FAIRY_PI_CLI 指定 cli.js 路径"));
}
