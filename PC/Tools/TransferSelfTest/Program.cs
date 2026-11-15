using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using MyAiAssistant.Services;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Transfer;

namespace TransferSelfTest;

/// <summary>
/// A two-machine self-test for the whole transfer chain.
///
/// Run it as <c>host</c> on one machine and <c>join</c> on the other. It uses the shipping code for
/// everything (pairing, the relay, WebRTC, the session, the approval queue) and writes a step log
/// plus a JSON summary, so a failure can be read afterwards instead of guessed at.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return 2;
        }

        var log = new TestLog(options.Role, options.WorkingRoot);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;

        app.Startup += async (_, _) =>
        {
            try
            {
                exitCode = options.Mode switch
                {
                    "host" => await RunHostAsync(options, log),
                    "web" => await RunWebAsync(options, log),
                    _ => await RunJoinAsync(options, log)
                };
            }
            catch (Exception ex)
            {
                log.Fail("未预期的异常：" + ex.GetType().Name + " " + ex.Message, ex);
                exitCode = 1;
            }
            finally
            {
                log.Finish();
                app.Shutdown();
            }
        };

        app.Run();
        log.WriteSummary();
        Console.WriteLine();
        Console.WriteLine("日志：" + log.LogPath);
        Console.WriteLine("摘要：" + log.SummaryPath);
        return exitCode;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        仙灵跨设备传输自检

          host 模式（先在这台机器运行）：
            TransferSelfTest.exe host [--port 9879] [--mb 8]

          join 模式（在另一台机器运行，使用 host 打印的配对串）：
            TransferSelfTest.exe join --host 192.168.1.5:9879 --pair "GRPxxx|INVxxx|123456" [--mb 8]

          web 模式（手机浏览器当第二台设备，无需安装 APK）：
            TransferSelfTest.exe web [--port 9880] [--mb 4]

        可选：
            --out <目录>   日志与测试文件的存放目录（默认在程序目录下 selftest-<时间>-<角色>）
        """);
    }

    // ---------------------------------------------------------------- host

    private static async Task<int> RunHostAsync(Options options, TestLog log)
    {
        log.Step("初始化配对库");
        Directory.CreateDirectory(options.WorkingRoot);

        using var pairing = new PairingService(
            new DeviceGroupStore(Path.Combine(options.WorkingRoot, "pairing"), new AppPairingSecretProtector()),
            new PairingInviteStore());
        pairing.HostDisplayName = SafeHostName();

        var (groupOk, groupMessage, group) = pairing.Store.CreateGroup("自检");
        if (!groupOk || group is null)
        {
            log.Fail("无法创建设备组：" + groupMessage);
            return 1;
        }

        var invite = pairing.CreateInvite(group.GroupId);
        if (!invite.Ok || invite.Invite is null)
        {
            log.Fail("无法生成配对码：" + invite.Message);
            return 1;
        }

        var endpoint = new PairingEndpointServer(pairing, options.Port);
        if (!endpoint.Start(IPAddress.Any))
        {
            log.Fail($"配对端口 {options.Port} 无法监听：{endpoint.LastError ?? "端口被占用或权限不足"}");
            return 1;
        }

        log.Info($"本机设备号 {pairing.Store.Identity.DeviceId}");
        log.Info($"监听 0.0.0.0:{endpoint.Port}（局域网地址 {string.Join(" / ", LocalAddresses())}）");
        var pairString = $"{group.GroupId}|{invite.Invite.InviteId}|{invite.Invite.Code}";
        log.Info("配对串（整段复制到另一台机器）：" + pairString);
        Console.WriteLine();
        Console.WriteLine("在另一台机器上运行：");
        Console.WriteLine($"  TransferSelfTest.exe join --host {LocalAddresses().FirstOrDefault() ?? "本机IP"}:{endpoint.Port} --pair \"{pairString}\"");
        Console.WriteLine();

        log.Step("等待对方加入（最多 10 分钟）");
        var member = await WaitForMemberAsync(pairing, group.GroupId, TimeSpan.FromMinutes(10), log);
        if (member is null) return 1;

        log.Ok($"对方已加入：{member.DisplayName} / {member.DeviceId}");

        var destination = Path.Combine(options.WorkingRoot, "received");
        Directory.CreateDirectory(destination);

        var center = new AgentControlCenter();
        var channelCount = 0;
        var control = new TransferControl(pairing, endpoint, center,
            async token =>
            {
                var index = Interlocked.Increment(ref channelCount);
                log.Step($"为第 {index} 条连接创建 WebRTC 通道");
                return await CreatePeerChannelAsync(log);
            },
            destination,
            approval: (offer, _) =>
            {
                log.Info($"对方想发送 {offer.Manifest.Files.Count} 个文件（{offer.TotalBytes} 字节），自检自动同意");
                return Task.FromResult(true);
            },
            isEnabled: () => true);

        control.Start();

        var sentFile = CreateTestFile(options.WorkingRoot, "host-发给-join.bin", options.Megabytes);
        var sentHash = TestLog.Sha256(sentFile);
        log.Info($"测试文件 {sentFile} 大小 {options.Megabytes} MB sha256={sentHash}");

        log.Step("主机 → 对方：发送测试文件");
        var started = DateTime.UtcNow;
        var send = await control.SendAsync(member.DeviceId, [sentFile]);
        if (!send.Ok)
        {
            log.Fail("发送失败：" + send.Message);
            return 1;
        }

        log.Ok($"发送完成（对方已确认落盘）：{send.Message}，用时 {(DateTime.UtcNow - started).TotalSeconds:0.0} 秒");
        log.Info("这一行要和对方日志里的「收到 …sha256」对上：" + sentHash);

        log.Step("对方 → 主机：等待回传文件（最多 5 分钟）");
        var received = await WaitForFileAsync(destination, "join-回传-host.bin", TimeSpan.FromMinutes(5), log);
        if (received is null) return 1;

        var receivedHash = TestLog.Sha256(received);
        log.Info($"收到对方的回传文件 sha256={receivedHash}");
        log.Info("这一行要和对方日志里的「回传文件 …sha256」对上：" + receivedHash);
        log.Ok("两个方向都完成了：本机既发出成功、也收到了对方的文件");

        control.Dispose();
        endpoint.Stop();
        return log.Passed ? 0 : 1;
    }

    // ---------------------------------------------------------------- join

    private static async Task<int> RunJoinAsync(Options options, TestLog log)
    {
        log.Step("解析配对串");
        var parts = options.Pair?.Split('|') ?? [];
        if (parts.Length != 3)
        {
            log.Fail("配对串格式不对，应为 GRP...|INV...|六位码");
            return 1;
        }

        var (groupId, inviteId, code) = (parts[0], parts[1], parts[2]);
        var relay = options.Host!;
        if (!relay.StartsWith("http", StringComparison.OrdinalIgnoreCase)) relay = "http://" + relay;

        log.Info($"设备组 {groupId} 邀请 {inviteId} 中继 {relay}");

        using var identity = DeviceIdentity.Create();
        using var joiner = new PairingJoinerSession(identity, inviteId, groupId, SafeHostName());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        log.Step("加入配对");
        var body = JsonSerializer.Serialize(new { code, request = joiner.Request },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(new Uri(relay), "/pairing/join"),
                new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Fail($"连不上对方：{ex.Message}（检查地址、端口、防火墙）");
            return 1;
        }

        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            log.Fail($"配对被拒绝（HTTP {(int)response.StatusCode}）：{payload}");
            return 1;
        }

        using var document = JsonDocument.Parse(payload);
        var answer = document.RootElement.GetProperty("response")
            .Deserialize<PairingResponse>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (answer is null)
        {
            log.Fail("配对应答无法解析：" + payload);
            return 1;
        }

        var joined = joiner.Accept(code, answer);
        if (!joined.Ok || joined.GroupKey is null || joined.Host is null)
        {
            log.Fail("配对失败：" + joined.Status.Message);
            return 1;
        }

        var groupKey = joined.GroupKey;
        var hostDeviceId = joined.Host.DeviceId;
        log.Ok($"配对完成，主机 {joined.Host.DisplayName} / {hostDeviceId}，组密钥指纹 {joined.Status.KeyFingerprint}");

        var destination = Path.Combine(options.WorkingRoot, "received");
        Directory.CreateDirectory(destination);

        using var signals = new SignalClient(relay, groupId, identity.DeviceId, hostDeviceId, groupKey);
        var agent = new PeerAgent(signals, groupKey, destination, hostDeviceId, log);
        agent.Start();

        log.Step("接收主机发来的测试文件（自动同意）");
        var incoming = await WaitForFileAsync(destination, "host-发给-join.bin", TimeSpan.FromMinutes(5), log);
        if (incoming is null) return 1;

        var incomingHash = TestLog.Sha256(incoming);
        log.Info($"收到主机的文件 sha256={incomingHash}");
        log.Info("这一行要和主机日志里的「测试文件 …sha256」对上：" + incomingHash);

        var backFile = CreateTestFile(options.WorkingRoot, "join-回传-host.bin", options.Megabytes);
        log.Info($"回传文件 {backFile} sha256={TestLog.Sha256(backFile)}");

        log.Step("对方 → 主机：回传文件");
        var started = DateTime.UtcNow;
        var send = await agent.SendAsync([backFile], SafeHostName());
        if (!send.Ok)
        {
            log.Fail("回传失败：" + send.Message);
            return 1;
        }

        log.Ok($"回传完成（主机已确认落盘）：{send.Message}，用时 {(DateTime.UtcNow - started).TotalSeconds:0.0} 秒");
        log.Info("回传文件的哈希，主机应显示为「收到对方的回传文件 sha256=" + TestLog.Sha256(backFile) + "」");

        agent.Dispose();
        return log.Passed ? 0 : 1;
    }

    // ---------------------------------------------------------------- web

    /// <summary>
    /// The phone-as-second-device mode: the PC serves a page over HTTPS, the phone opens it, and the
    /// transfer runs with the phone's own approval. No pairing handshake here — the page is given the
    /// group key because this is a transport test, and pairing is covered by the automated suites.
    /// </summary>
    private static async Task<int> RunWebAsync(Options options, TestLog log)
    {
        log.Step("初始化配对库与设备组");
        Directory.CreateDirectory(options.WorkingRoot);

        using var pairing = new PairingService(
            new DeviceGroupStore(Path.Combine(options.WorkingRoot, "pairing"), new AppPairingSecretProtector()),
            new PairingInviteStore());
        pairing.HostDisplayName = SafeHostName();

        var (groupOk, groupMessage, group) = pairing.Store.CreateGroup("手机自检");
        if (!groupOk || group is null)
        {
            log.Fail("无法创建设备组：" + groupMessage);
            return 1;
        }

        using var phone = DeviceIdentity.Create();
        var (added, addMessage, _) = pairing.Store.AddMember(group.GroupId, new PairingRequest(
            "INVWEB", group.GroupId, phone.DeviceId, "手机（网页）", phone.SigningPublicKey,
            phone.AgreementPublicKey, phone.AgreementPublicKey, "web"));
        if (!added)
        {
            log.Fail("无法登记手机设备：" + addMessage);
            return 1;
        }

        var groupKey = pairing.Store.TryGetGroupKey(group.GroupId)!;
        var endpoint = new PairingEndpointServer(pairing, options.Port);
        var hostDeviceId = pairing.Store.Identity.DeviceId;

        using var web = new WebHost(endpoint,
            new SessionInfo(group.GroupId, groupKey, hostDeviceId, pairing.HostDisplayName,
                phone.DeviceId, "手机（网页）"),
            options.Port);

        if (!web.Start())
        {
            log.Fail($"网页端口 {options.Port} 无法监听（可能被占用），用 --port 换一个");
            return 1;
        }

        var addresses = LocalAddresses();
        log.Info($"本机设备号 {hostDeviceId}，手机设备号 {phone.DeviceId}");
        log.Info("在手机浏览器里打开下面的地址（同一局域网，无需安装任何东西）：");
        foreach (var address in addresses) Console.WriteLine($"    http://{address}:{web.Port}/");
        Console.WriteLine();

        // With --verify the tool opens the page itself in WebView2 (the same Chromium engine a phone
        // uses), so the whole path can be checked before involving a real phone.
        var pageUrl = $"http://{(addresses.FirstOrDefault() ?? "127.0.0.1")}:{web.Port}/";
        if (options.Verify) pageUrl = $"http://127.0.0.1:{web.Port}/";
        var verifier = options.Verify ? await PageVerifier.StartAsync(pageUrl, log) : null;

        log.Step(options.Verify ? "本机自检：自动打开页面并答应接收" : "等待手机打开页面（最多 10 分钟）");
        var opened = await Task.WhenAny(web.PageOpened, Task.Delay(TimeSpan.FromMinutes(10)));
        if (opened != web.PageOpened)
        {
            log.Fail("等待超时：手机没有打开页面（检查手机和电脑是否在同一网络、端口是否被防火墙拦）");
            return 1;
        }

        log.Ok("手机已打开页面");

        var destination = Path.Combine(options.WorkingRoot, "received");
        Directory.CreateDirectory(destination);

        var center = new AgentControlCenter();
        var control = new TransferControl(pairing, endpoint, center,
            async _ =>
            {
                log.Step("创建 WebRTC 通道");
                return await CreatePeerChannelAsync(log);
            },
            destination,
            approval: (offer, _) =>
            {
                log.Info($"手机想发送 {offer.Manifest.Files.Count} 个文件，自检自动同意");
                return Task.FromResult(true);
            },
            isEnabled: () => true);

        control.Start();

        var file = CreateTestFile(options.WorkingRoot, "pc-发给-手机.bin", options.Megabytes);
        var fileHash = TestLog.Sha256(file);
        log.Info($"测试文件 {file} 大小 {options.Megabytes} MB sha256={fileHash}");

        log.Step("发送给手机：等你在手机上点「允许接收」");
        var started = DateTime.UtcNow;
        var result = await control.SendAsync(phone.DeviceId, [file]);
        if (!result.Ok)
        {
            log.Fail("发送失败：" + result.Message);
            return 1;
        }

        log.Ok($"手机已确认收到并通过哈希校验：{result.Message}，用时 {(DateTime.UtcNow - started).TotalSeconds:0.0} 秒");
        log.Info("手机上应显示「校验通过，可以保存」和同一个 sha256：" + fileHash);

        if (verifier is not null && !await verifier.PassAsync(log))
        {
            log.Fail("页面自检没有通过（见上面的页面状态）");
            return 1;
        }

        control.Dispose();
        return log.Passed ? 0 : 1;
    }

    // ---------------------------------------------------------------- shared

    private static async Task<GroupMember?> WaitForMemberAsync(PairingService pairing, string groupId,
        TimeSpan timeout, TestLog log)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var group = pairing.Store.Find(groupId);
            if (group is { Members.Count: > 0 }) return group.Members[0];
            await Task.Delay(300);
        }

        log.Fail("等待超时：对方没有加入（检查配对串是否抄错、对方能否连到这台机器的端口）");
        return null;
    }

    private static async Task<string?> WaitForFileAsync(string directory, string name, TimeSpan timeout, TestLog log)
    {
        var path = Path.Combine(directory, name);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                // Give the writer a moment to finish the move.
                await Task.Delay(200);
                if (File.Exists(path)) return path;
            }

            await Task.Delay(200);
        }

        log.Fail($"等待超时：{name} 没有到达（目录 {directory}）");
        return null;
    }

    private static async Task<ITransferChannel> CreatePeerChannelAsync(TestLog log)
    {
        var dispatcher = Application.Current?.Dispatcher
                         ?? throw new InvalidOperationException("没有 UI 线程，无法创建 WebRTC 面");
        var cache = Path.Combine(AppContext.BaseDirectory, "webview2-selftest-" + Environment.ProcessId);
        var peer = await dispatcher.InvokeAsync(() => WebRtcPeer.CreateAsync(cache)).Task.Unwrap();
        peer.Failed += message => log.Fail("WebRTC 面报错：" + message);
        return peer;
    }

    private static string CreateTestFile(string directory, string name, int megabytes)
    {
        var path = Path.Combine(directory, name);
        if (File.Exists(path)) return path;

        var bytes = RandomNumberGenerator.GetBytes(Math.Max(1, megabytes) * 1024 * 1024);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string SafeHostName()
    {
        var name = Environment.MachineName;
        return string.IsNullOrWhiteSpace(name) ? "未命名设备" : name;
    }

    internal static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .ToArray();
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- options

    internal sealed record Options(string Mode, string Role, string WorkingRoot, int Port, int Megabytes,
        string? Host, string? Pair, bool Verify)
    {
        public static Options? Parse(string[] args)
        {
            if (args.Length == 0) return null;

            var mode = args[0].ToLowerInvariant();
            if (mode is not ("host" or "join" or "web")) return null;

            var port = 9879;
            var megabytes = 8;
            string? host = null;
            string? pair = null;
            string? output = null;
            var verify = false;

            for (var index = 1; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--port" when index + 1 < args.Length:
                        int.TryParse(args[++index], out port);
                        break;
                    case "--mb" when index + 1 < args.Length:
                        int.TryParse(args[++index], out megabytes);
                        break;
                    case "--host" when index + 1 < args.Length:
                        host = args[++index];
                        break;
                    case "--pair" when index + 1 < args.Length:
                        pair = args[++index];
                        break;
                    case "--out" when index + 1 < args.Length:
                        output = args[++index];
                        break;
                    case "--verify":
                        verify = true;
                        break;
                }
            }

            if (mode == "join" && (host is null || pair is null)) return null;
            if (mode == "web" && port <= 0) return null;

            var root = output ?? Path.Combine(AppContext.BaseDirectory,
                $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}-{mode}");
            Directory.CreateDirectory(root);

            return new Options(mode, mode, Path.GetFullPath(root), port <= 0 ? 9879 : port,
                Math.Clamp(megabytes, 1, 256), host, pair, verify);
        }
    }
}
