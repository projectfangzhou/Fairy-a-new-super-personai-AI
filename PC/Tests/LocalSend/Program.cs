using System.Net;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services.Http;
using MyAiAssistant.Services.LocalSend;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var tempRoot = Path.Combine(Path.GetTempPath(), "fairyai-localsend-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    // ---- 1. The announce payload is the published protocol, not lookalike JSON ----
    {
        var self = new LocalSendDevice("仙灵-PC", LocalSendProtocol.Version, "Windows", "desktop",
            "fingerprint-abc", 53317, "http", false);
        var json = LocalSendProtocol.BuildAnnounce(self);

        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("alias").GetString() == "仙灵-PC", "announce carries the alias");
            Assert(root.GetProperty("version").GetString() == "2.0", "announce declares version 2.0");
            Assert(root.GetProperty("port").GetInt32() == 53317, "the port is a number, as the protocol expects");
            Assert(root.GetProperty("protocol").GetString() == "http", "the protocol is declared");
            Assert(root.GetProperty("fingerprint").GetString() == "fingerprint-abc", "the fingerprint is declared");
            Assert(root.GetProperty("deviceType").GetString() == "desktop", "the device type is declared");
            Assert(root.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.False,
                "download is an explicit false");
        }

        var parsed = LocalSendProtocol.ParseAnnounce(json, "192.168.1.5");
        Assert(parsed is not null, "our own announce parses back");
        Assert(parsed!.Endpoint == "http://192.168.1.5:53317", "the endpoint is derived from the datagram source");
        Assert(parsed.Address == "192.168.1.5", "the source address is kept");
    }

    // ---- 2. Bad or incomplete announces are refused, not half-trusted ----
    {
        Assert(LocalSendProtocol.ParseAnnounce("{}", "1.2.3.4") is null, "an empty announce is refused");
        Assert(LocalSendProtocol.ParseAnnounce("not json", "1.2.3.4") is null, "a malformed announce is refused");
        Assert(LocalSendProtocol.ParseAnnounce("""{"alias":"x","fingerprint":"f","port":0}""", "1.2.3.4") is null,
            "a zero port is refused");
        Assert(LocalSendProtocol.ParseAnnounce("""{"alias":"x","fingerprint":"f","port":70000}""", "1.2.3.4") is null,
            "an out-of-range port is refused");
        Assert(LocalSendProtocol.ParseAnnounce("""{"alias":"x","port":53317}""", "1.2.3.4") is null,
            "a missing fingerprint is refused");
        Assert(LocalSendProtocol.ParseAnnounce("""{"fingerprint":"f","port":53317}""", "1.2.3.4") is null,
            "a missing alias is refused");
        Assert(LocalSendProtocol.ParseAnnounce("""{"alias":"x","fingerprint":"f","port":53317,"protocol":"ftp"}""", "1.2.3.4") is null,
            "an unknown protocol is refused");

        var stringPort = LocalSendProtocol.ParseAnnounce(
            """{"alias":"x","fingerprint":"f","port":"53318","protocol":"https"}""", "1.2.3.4");
        Assert(stringPort is not null && stringPort.Port == 53318, "a port sent as text is accepted");
        Assert(stringPort!.Endpoint == "https://1.2.3.4:53318", "https peers keep their scheme");

        Assert(LocalSendProtocol.GuessFileType("a.PNG") == "image/png", "file types are guessed case-insensitively");
        Assert(LocalSendProtocol.GuessFileType("noext") == "application/octet-stream", "unknown types fall back to bytes");
        Assert(LocalSendProtocol.CreateFingerprint().Length == 32, "fingerprints are 32 hex characters");
        Assert(LocalSendProtocol.CreateFingerprint() != LocalSendProtocol.CreateFingerprint(),
            "fingerprints are random per install");
    }

    // ---- 3. Discovery: peers appear, expire, and our own datagram is ignored ----
    {
        var self = new LocalSendDevice("self", LocalSendProtocol.Version, "Windows", "desktop",
            "self-fp", 53317, "http", false);
        using var discovery = new LocalSendDiscovery(new FakeTransport(), self);

        var peerJson = Encoding.UTF8.GetBytes(
            """{"alias":"Phone","version":"2.0","deviceModel":"Pixel","deviceType":"mobile","fingerprint":"peer-1","port":53317,"protocol":"http","download":false}""");
        var peer = discovery.Observe(peerJson, "192.168.1.9");
        Assert(peer is not null && peer.Alias == "Phone", "an announce becomes a peer");
        Assert(discovery.Peers().Count == 1, "the peer is listed");
        Assert(discovery.Peers()[0].Endpoint == "http://192.168.1.9:53317", "the peer endpoint is usable");

        Assert(discovery.Observe(peerJson, "192.168.1.9") is not null && discovery.Peers().Count == 1,
            "the same device announcing twice is one peer");

        var selfJson = Encoding.UTF8.GetBytes(LocalSendProtocol.BuildAnnounce(self));
        Assert(discovery.Observe(selfJson, "127.0.0.1") is null, "our own announce is not a peer");
        Assert(discovery.IgnoredAnnouncements == 1, "the ignored datagram is counted");

        Assert(discovery.Observe(Encoding.UTF8.GetBytes("{}"), "192.168.1.9") is null,
            "a malformed announce is ignored without throwing");
    }

    // Expiry is judged against the announce time, not the query time.
    {
        var self = new LocalSendDevice("self", LocalSendProtocol.Version, "Windows", "desktop",
            "self-fp", 53317, "http", false);
        using var discovery = new LocalSendDiscovery(new FakeTransport(), self, expiry: TimeSpan.FromMilliseconds(60));
        discovery.Observe(Encoding.UTF8.GetBytes(
            """{"alias":"Old","version":"2.0","deviceModel":"x","deviceType":"desktop","fingerprint":"old","port":53317,"protocol":"http"}"""), "192.168.1.9");
        Assert(discovery.Peers().Count == 1, "a fresh peer is listed");
        await Task.Delay(120);
        Assert(discovery.Peers().Count == 0, "a peer that stopped announcing disappears");
    }

    // ---- 4. Sending: prepare-upload, per-file token, real bytes ----
    {
        var received = new List<(string Path, string? Session, string? FileId, string? Token, int Bytes)>();
        var prepareStatus = HttpStatusCode.OK;
        var failSecondUpload = false;

        var peerServer = new LocalHttpServer(async request =>
        {
            var path = request.Path;
            if (path.StartsWith("/api/localsend/v2/prepare-upload", StringComparison.Ordinal))
            {
                if (prepareStatus != HttpStatusCode.OK)
                    return new HttpResponseData((int)prepareStatus, "{}");

                using var document = JsonDocument.Parse(request.Body);
                var files = document.RootElement.GetProperty("files");
                var tokens = new Dictionary<string, string>();
                foreach (var file in files.EnumerateObject())
                    tokens[file.Name] = "token-" + file.Name;

                var payload = JsonSerializer.Serialize(new { sessionId = "session-1", files = tokens });
                return new HttpResponseData(200, payload);
            }

            if (path.StartsWith("/api/localsend/v2/upload", StringComparison.Ordinal))
            {
                var query = ParseQuery(path);
                var isSecond = received.Count(r => r.Path.StartsWith("/api/localsend/v2/upload", StringComparison.Ordinal)) >= 1;
                received.Add((path, query.GetValueOrDefault("sessionId"), query.GetValueOrDefault("fileId"),
                    query.GetValueOrDefault("token"), request.Body.Length));

                if (failSecondUpload && isSecond)
                    return new HttpResponseData(500, "{}");
                return new HttpResponseData(200, "{}");
            }

            return new HttpResponseData(404, "{}");
        });

        Assert(peerServer.Start(0), "the fake peer binds");
        try
        {
            var peer = new LocalSendDevice("Phone", LocalSendProtocol.Version, "Pixel", "mobile",
                "peer-1", peerServer.Port, "http", false) { Address = "127.0.0.1" };
            var self = new LocalSendDevice("PC", LocalSendProtocol.Version, "Windows", "desktop",
                "self-fp", 53317, "http", false);

            var first = Path.Combine(tempRoot, "one.txt");
            var second = Path.Combine(tempRoot, "two.bin");
            await File.WriteAllTextAsync(first, "hello localsend");
            await File.WriteAllBytesAsync(second, new byte[2048]);

            using var client = new LocalSendClient();
            var result = await client.SendFilesAsync(peer, self, new[] { first, second });
            Assert(result.Ok, "the transfer succeeds against a well-behaved peer: " + result.Message);
            Assert(result.UploadedFiles == 2, "both files were uploaded");
            Assert(result.SessionId == "session-1", "the session id from prepare is used");

            var uploads = received.Where(r => r.Path.StartsWith("/api/localsend/v2/upload", StringComparison.Ordinal)).ToArray();
            Assert(uploads.Length == 2, "one upload per file");
            Assert(uploads.All(u => u.Session == "session-1"), "every upload carries the session");
            Assert(uploads.Select(u => u.FileId).OrderBy(x => x).SequenceEqual(new[] { "1", "2" }),
                "each upload carries its file id");
            Assert(uploads.All(u => u.Token is not null && u.Token.StartsWith("token-", StringComparison.Ordinal)),
                "each upload carries its token");
            Assert(uploads.Sum(u => u.Bytes) == "hello localsend".Length + 2048, "the bytes actually arrive");

            // A refusing peer is reported as a refusal, not as a silent success.
            prepareStatus = HttpStatusCode.Forbidden;
            var refused = await client.SendFilesAsync(peer, self, new[] { first });
            Assert(!refused.Ok && refused.Message.Contains("拒绝", StringComparison.Ordinal),
                "a peer that refuses is reported as refusing");

            prepareStatus = HttpStatusCode.OK;
            failSecondUpload = true;
            received.Clear();
            var partial = await client.SendFilesAsync(peer, self, new[] { first, second });
            Assert(!partial.Ok, "a partial batch is not success");
            Assert(partial.UploadedFiles == 1, "the partial batch reports how many files made it");

            // An unreachable peer never throws out of the client.
            var unreachable = new LocalSendDevice("Gone", LocalSendProtocol.Version, "x", "desktop",
                "gone", 1, "http", false) { Address = "127.0.0.1" };
            var failed = await client.SendFilesAsync(unreachable, self, new[] { first });
            Assert(!failed.Ok && !failed.Message.Contains("Unhandled", StringComparison.Ordinal),
                "an unreachable peer yields a failure result");
        }
        finally
        {
            peerServer.Stop();
        }
    }

    // ---- 5. Receiving: consent first, safe names, declared sizes enforced ----
    {
        var saveDir = Path.Combine(tempRoot, "received");
        Directory.CreateDirectory(saveDir);

        LocalSendIncoming? seen = null;
        var receiver = new LocalSendReceiver(
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "self-fp", 53317, saveDir),
            new DelegateConsent(incoming =>
            {
                seen = incoming;
                return true;
            }));

        var receivedPaths = new List<string>();
        receiver.FilesReceived += paths => receivedPaths.AddRange(paths);

        Assert(receiver.Start(System.Net.IPAddress.Loopback, 0), "the receiver binds on loopback");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var baseUrl = $"http://127.0.0.1:{receiver.Port}";

            using (var info = await http.GetAsync($"{baseUrl}/api/localsend/v2/info"))
            {
                Assert(info.IsSuccessStatusCode, "the info endpoint answers");
                var body = await info.Content.ReadAsStringAsync();
                Assert(body.Contains("\"alias\"", StringComparison.Ordinal), "the info endpoint reports a device");
                Assert(body.Contains("\"protocol\":\"http\"", StringComparison.Ordinal), "the info endpoint declares http");
            }

            var fileOne = Path.Combine(tempRoot, "photo.txt");
            await File.WriteAllTextAsync(fileOne, "receiving works");
            var self = new LocalSendDevice("Phone", LocalSendProtocol.Version, "Pixel", "mobile",
                "peer-1", 53317, "http", false);
            var peer = new LocalSendDevice("仙灵-PC", LocalSendProtocol.Version, "Windows", "desktop",
                "self-fp", receiver.Port, "http", false) { Address = "127.0.0.1" };

            using var client = new LocalSendClient();
            var sent = await client.SendFilesAsync(peer, self, new[] { fileOne });
            Assert(sent.Ok, "a real send against our own receiver succeeds: " + sent.Message);
            Assert(seen is not null && seen.SenderAlias == "Phone", "the sender was shown to the user before writing");
            Assert(receivedPaths.Count == 1, "the completion event reports the saved file");
            var savedName = Path.GetFileName(receivedPaths[0]);
            Assert(savedName == "photo.txt", "the original file name is kept");
            Assert(File.ReadAllText(receivedPaths[0]) == "receiving works", "the received bytes match");

            // A second transfer of the same name must not overwrite the first.
            var again = await client.SendFilesAsync(peer, self, new[] { fileOne });
            Assert(again.Ok, "a second transfer also succeeds");
            Assert(File.Exists(Path.Combine(saveDir, "photo.txt")) &&
                   File.Exists(Path.Combine(saveDir, "photo (2).txt")),
                "an existing file is not overwritten; a unique name is used");

            // Declining answers 403, which is what the official sender treats as refusal.
            using var declining = new LocalSendReceiver(
                new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "self-fp", 53317, saveDir),
                new DelegateConsent(_ => false));
            Assert(declining.Start(System.Net.IPAddress.Loopback, 0), "the declining receiver binds");
            try
            {
                var decliningPeer = new LocalSendDevice("仙灵-PC", LocalSendProtocol.Version, "Windows", "desktop",
                    "self-fp", declining.Port, "http", false) { Address = "127.0.0.1" };
                var refused = await client.SendFilesAsync(decliningPeer, self, new[] { fileOne });
                Assert(!refused.Ok && refused.Message.Contains("拒绝", StringComparison.Ordinal),
                    "a declined transfer is reported as refused");
            }
            finally { declining.Stop(); }

            // A name that tries to escape the save directory is neutralised, not trusted.
            var traversal = JsonSerializer.Serialize(new
            {
                info = new
                {
                    alias = "Evil", version = "2.0", deviceModel = "x", deviceType = "desktop",
                    fingerprint = "evil", port = 1, protocol = "http", download = false
                },
                files = new Dictionary<string, object>
                {
                    ["1"] = new { id = "1", fileName = "../../escape.txt", size = 2, fileType = "text/plain" }
                }
            });
            using (var content = new StringContent(traversal, Encoding.UTF8, "application/json"))
            {
                var prepare = await http.PostAsync($"{baseUrl}/api/localsend/v2/prepare-upload", content);
                Assert(prepare.IsSuccessStatusCode, "a traversal name is accepted as a transfer after sanitising");
                var payload = JsonDocument.Parse(await prepare.Content.ReadAsStringAsync()).RootElement;
                var sessionId = payload.GetProperty("sessionId").GetString()!;
                var token = payload.GetProperty("files").GetProperty("1").GetString()!;

                using var upload = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/api/localsend/v2/upload?sessionId={sessionId}&fileId=1&token={token}")
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hi"))
                };
                using var uploaded = await http.SendAsync(upload);
                Assert(uploaded.IsSuccessStatusCode, "the sanitised file is accepted");
                Assert(File.Exists(Path.Combine(saveDir, "escape.txt")), "the file lands inside the save directory");
                Assert(!File.Exists(Path.Combine(tempRoot, "escape.txt")) &&
                       !File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")),
                    "the traversal target was not written");
            }

            // A wrong token is refused.
            using (var badToken = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/api/localsend/v2/upload?sessionId=whatever&fileId=1&token=nope")
            {
                Content = new ByteArrayContent(new byte[1])
            })
            {
                var response = await http.SendAsync(badToken);
                Assert((int)response.StatusCode == 403, "an upload with an unknown session or token is refused");
            }
        }
        finally
        {
            receiver.Stop();
        }

        // Size limits are enforced before the transfer starts.
        using var limited = new LocalSendReceiver(
            new LocalSendReceiveOptions("仙灵-PC", "Windows", "desktop", "self-fp", 53317, saveDir)
            {
                MaxFileBytes = 4
            },
            new DelegateConsent(_ => true));
        Assert(limited.Start(System.Net.IPAddress.Loopback, 0), "the limited receiver binds");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bigFile = Path.Combine(tempRoot, "too-big.bin");
            await File.WriteAllBytesAsync(bigFile, new byte[64]);
            var self = new LocalSendDevice("Phone", LocalSendProtocol.Version, "Pixel", "mobile",
                "peer-1", 53317, "http", false);
            var peer = new LocalSendDevice("仙灵-PC", LocalSendProtocol.Version, "Windows", "desktop",
                "self-fp", limited.Port, "http", false) { Address = "127.0.0.1" };
            using var client = new LocalSendClient();
            var refused = await client.SendFilesAsync(peer, self, new[] { bigFile });
            Assert(!refused.Ok && refused.Message.Contains("拒绝", StringComparison.Ordinal),
                "a file over the receive limit is refused up front");
        }
        finally { limited.Stop(); }
    }

    // ---- 6. The real UDP transport either works or says why, never silently ----
    {
        using var transport = new UdpLocalSendTransport();
        try
        {
            transport.Start(0);
            Assert(transport.IsRunning, "the multicast transport reports itself running after a bind");
        }
        catch (Exception)
        {
            Assert(!transport.IsRunning, "a failed bind leaves the transport stopped");
            Assert(!string.IsNullOrWhiteSpace(transport.LastError),
                "a failed bind reports why instead of failing silently");
            Console.WriteLine($"  note: multicast unavailable here ({transport.LastError})");
        }
    }

    Console.WriteLine($"All {checks} LocalSend checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED after {checks} checks: {ex.Message}");
    return 1;
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch (IOException) { }
}

static Dictionary<string, string> ParseQuery(string path)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var index = path.IndexOf('?');
    if (index < 0) return result;
    foreach (var pair in path[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = pair.IndexOf('=');
        if (separator <= 0) continue;
        result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
    }
    return result;
}

internal sealed class DelegateConsent(Func<LocalSendIncoming, bool> decide) : IReceiveConsent
{
    public Task<bool> ConfirmAsync(LocalSendIncoming incoming, CancellationToken cancellationToken) =>
        Task.FromResult(decide(incoming));
}

internal sealed class FakeTransport : ILocalSendTransport
{
    public List<byte[]> Sent { get; } = new();

    public void Start(int port) { }

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        Sent.Add(payload);
        return Task.CompletedTask;
    }

    public Task<(byte[] Data, string? SourceAddress)?> ReceiveAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
            _ => ((byte[], string?)?)null, TaskScheduler.Default);

    public void Dispose() { }
}
