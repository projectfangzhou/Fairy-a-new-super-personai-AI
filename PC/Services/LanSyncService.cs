using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services;

/// <summary>
/// LAN sync and file transfer service using LocalSend-style HTTP protocol.
/// Devices announce themselves via UDP broadcast, and files/configs are
/// exchanged over a lightweight HTTP server.
/// </summary>
public class LanSyncService : ISyncService, IFileTransferService
{
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _lanEnabled; // LAN mode: when true, binds to all interfaces
    // Rate limiting for /pair endpoint
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime Reset)> _pairAttempts = new();
    // Paired device sessions
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pairedDevices = new();

    /// <summary>
    /// Check whether the requesting device may use the paired endpoints.
    ///
    /// The device name is <em>not</em> the credential: a name is guessed in one try (it is usually the
    /// model of the phone). What counts is the bearer token issued at pairing, whose hash is stored
    /// here and which can be revoked per device. A request that carries only the name is refused.
    /// </summary>
    private bool IsDevicePaired(HttpListenerRequest request)
    {
        var fromDevice = request.Headers["X-From-Device"];
        var token = request.Headers["X-Device-Token"];
        return _deviceTokens.Verify(fromDevice, token);
    }

    /// <summary>Rate limit /pair attempts: max 5 per device per 10 minutes.</summary>
    private static bool CheckPairRateLimit(string fromDevice)
    {
        var entry = _pairAttempts.GetOrAdd(fromDevice, _ => (0, DateTime.UtcNow.AddMinutes(10)));
        if (DateTime.UtcNow > entry.Reset)
        {
            _pairAttempts[fromDevice] = (1, DateTime.UtcNow.AddMinutes(10));
            return true;
        }
        if (entry.Count >= 5) return false;
        _pairAttempts[fromDevice] = (entry.Count + 1, entry.Reset);
        return true;
    }

    /// <summary>Mark a device as paired.</summary>
    private static void MarkPaired(string deviceName)
    {
        _pairedDevices[deviceName] = DateTime.UtcNow;
    }

    /// <summary>Snapshot of currently paired devices (name → paired-at UTC).</summary>
    public static IReadOnlyDictionary<string, DateTime> GetPairedDevices() =>
        new Dictionary<string, DateTime>(_pairedDevices);
    private readonly string _logPath;
    private readonly SyncConfig _config;
    private readonly PairedDeviceTokens _deviceTokens;
    private string _localEndpoint = "";

    /// <summary>
    /// Where the summary endpoint gets task state. Set by the app; left null the endpoint answers
    /// with an empty list rather than inventing tasks.
    /// </summary>
    public Func<IReadOnlyList<TaskSummary>>? SummarySource { get; set; }

    /// <summary>
    /// Task list and pending approvals for a paired phone. Set by the app; left null the endpoints
    /// answer "unavailable" instead of pretending there is nothing to show.
    /// </summary>
    public PairedTaskBridge? TaskBridge { get; set; }

    /// <summary>Note sync with a paired phone; null means the endpoint reports it is unavailable.</summary>
    public PairedNoteBridge? NoteBridge { get; set; }

    public string ServiceName => "LAN (LocalSend)";
    public bool IsRunning => _isRunning;
    public string LocalEndpoint => _localEndpoint;

    public event EventHandler<SyncMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<FileTransferEventArgs>? FileReceived;
    public event EventHandler<FileTransferProgressEventArgs>? ProgressChanged;

    public LanSyncService(PairedDeviceTokens? deviceTokens = null)
    {
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fairy.log");
        _config = ConfigManager.Load().Sync;
        // Injectable so a bench can point the store at a scratch directory; the app keeps the shared one.
        _deviceTokens = deviceTokens ?? PairedDeviceTokens.Shared;
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            var port = _config.FileServerPort;

            _httpListener = new HttpListener();
            // Security: bind localhost by default; LAN mode requires explicit enable
            if (_lanEnabled)
                _httpListener.Prefixes.Add($"http://+:{port}/");
            else
                _httpListener.Prefixes.Add($"http://localhost:{port}/");
            _httpListener.Start();
            _isRunning = true;
            _localEndpoint = $"http://{GetLocalIPAddress()}:{port}";

            Log($"LAN file server started on {_localEndpoint}");
            _ = ListenForRequestsAsync(_cts.Token);
            _ = BroadcastAnnouncementAsync(_cts.Token);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log($"LAN start error: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _isRunning = false;

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        Log("LAN file server stopped");
        return Task.CompletedTask;
    }

    private async Task ListenForRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener != null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = HandleRequestAsync(context, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.HttpMethod == "GET" && request.Url?.LocalPath == "/announce")
            {
                // Device announcement
                var info = new
                {
                    deviceId = Environment.MachineName,
                    deviceName = _config.DeviceName,
                    endpoint = _localEndpoint,
                    version = "1.3.0"
                };
                var json = JsonSerializer.Serialize(info);
                var buffer = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(buffer, ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/send")
            {
                // Auth: require paired device
                if (!IsDevicePaired(request))
                {
                    response.StatusCode = 403;
                    return;
                }
                // Receive file — sanitize filename to prevent path traversal (CVE fix)
                var rawFileName = request.Headers["X-File-Name"] ?? "unknown";
                var fileName = Path.GetFileName(rawFileName); // strips directory components
                if (string.IsNullOrWhiteSpace(fileName) || fileName == ".." || fileName == ".")
                    fileName = "unknown_" + Guid.NewGuid().ToString("N")[..8];
                // Additional: reject if still contains invalid chars
                foreach (var c in Path.GetInvalidFileNameChars())
                    fileName = fileName.Replace(c, '_');
                var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "received_files");
                Directory.CreateDirectory(tempDir);
                var tempPath = Path.Combine(tempDir, fileName);

                using (var fs = File.Create(tempPath))
                {
                    await request.InputStream.CopyToAsync(fs, ct);
                }

                var fileSize = new FileInfo(tempPath).Length;
                Log($"File received: {fileName} ({fileSize} bytes)");

                FileReceived?.Invoke(this, new FileTransferEventArgs
                {
                    FileName = fileName,
                    TempPath = tempPath,
                    Size = fileSize
                });

                var okBuffer = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(okBuffer, ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/command")
            {
                // Auth: require paired device
                if (!IsDevicePaired(request))
                {
                    response.StatusCode = 403;
                    return;
                }
                // Receive command
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var msg = JsonSerializer.Deserialize<CommandMessage>(body);

                if (msg != null)
                {
                    MessageReceived?.Invoke(this, new SyncMessageReceivedEventArgs
                    {
                        FromDeviceId = msg.FromDeviceId,
                        Command = msg.Command,
                        Payload = msg.Payload
                    });
                }

                var okBuffer = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(okBuffer, ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/pair")
            {
                // Rate limiting: max 5 attempts per device per 10 minutes
                var fromDevice = request.Headers["X-From-Device"] ?? "unknown";
                if (!CheckPairRateLimit(fromDevice))
                {
                    Log($"Pair rate limited: {fromDevice}");
                    response.StatusCode = 429;
                    return;
                }

                // Pairing verification
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var doc = JsonDocument.Parse(body);
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var pairFromDevice = request.Headers["X-From-Device"] ?? "unknown";

                var config = ConfigManager.Load();
                var storedHash = config.Sync.PairingCode;

                if (string.IsNullOrEmpty(storedHash))
                {
                    // PC 端尚未生成配对码 —— 要求用户先在"设备互联"窗口生成并在手机端输入
                    Log($"Pair rejected (no pairing code set on PC): {fromDevice}");
                    response.StatusCode = 409;
                    var noCodeBuffer = Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { success = false, error = "no_code" }));
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(noCodeBuffer, ct);
                    return;
                }

                bool success = ConfigManager.VerifyPairingCode(code, storedHash);
                Log($"Pairing verification: {success}, device: {fromDevice}");
                if (success) MarkPaired(fromDevice);

                // A device name is not a secret, so a new endpoint must not treat it as one. Pairing
                // issues a bearer token here; only its hash is kept, and it is handed out once.
                var issued = success ? _deviceTokens.Issue(fromDevice) : null;

                var result = JsonSerializer.Serialize(new { success, deviceToken = issued });
                var resultBuffer = Encoding.UTF8.GetBytes(result);
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(resultBuffer, ct);
            }
            else if (request.HttpMethod == "GET" && request.Url?.LocalPath == "/summary")
            {
                // Task summaries for a paired phone. Opt-in on the PC side, token-authenticated, and
                // the payload is the summary shape only — see SummarySyncPayload.
                var config = ConfigManager.Load();
                var summaryDevice = request.Headers["X-From-Device"];
                var summaryToken = request.Headers["X-Device-Token"];
                var rejection = SummarySyncAccess.RejectReason(
                    config.Sync.SummarySyncEnabled, _deviceTokens.Verify(summaryDevice, summaryToken));

                if (rejection is not null)
                {
                    Log($"Summary request rejected ({rejection}): {summaryDevice ?? "unknown"}");
                    response.StatusCode = 403;
                    var denied = Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { success = false, error = rejection }));
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(denied, ct);
                    return;
                }

                var summaries = SummarySource?.Invoke() ?? Array.Empty<TaskSummary>();
                var payload = Encoding.UTF8.GetBytes(SummarySyncPayload.Build(summaries));
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(payload, ct);
            }
            else if (request.HttpMethod == "GET" && request.Url?.LocalPath == "/desk")
            {
                var bridge = TaskBridge;
                if (bridge is null)
                {
                    response.StatusCode = 503;
                    await WriteJsonAsync(response, new { success = false, error = "pair_control_unavailable" }, ct);
                    return;
                }

                var refusal = bridge.RejectReason(request.Headers["X-From-Device"], request.Headers["X-Device-Token"]);
                if (refusal is not null)
                {
                    Log($"Desk request rejected ({refusal}): {request.Headers["X-From-Device"] ?? "unknown"}");
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new { success = false, error = refusal }, ct);
                    return;
                }

                var desk = bridge.Read();
                await WriteJsonAsync(response, new
                {
                    success = true,
                    generatedAt = desk.GeneratedAt,
                    tasks = desk.Tasks.Select(task => new
                    {
                        taskId = task.TaskId,
                        title = task.Title,
                        state = task.State,
                        canPause = task.CanPause,
                        canStop = task.CanStop,
                        updatedAt = task.UpdatedAt
                    }),
                    pending = desk.Pending.Select(item => new
                    {
                        interactionId = item.InteractionId,
                        taskId = item.TaskId,
                        kind = item.Kind,
                        prompt = item.Prompt,
                        risk = item.Risk,
                        createdAt = item.CreatedAt
                    })
                }, ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/desk/answer")
            {
                var bridge = TaskBridge;
                if (bridge is null)
                {
                    response.StatusCode = 503;
                    await WriteJsonAsync(response, new { success = false, error = "pair_control_unavailable" }, ct);
                    return;
                }

                var refusal = bridge.RejectReason(request.Headers["X-From-Device"], request.Headers["X-Device-Token"]);
                if (refusal is not null)
                {
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new { success = false, error = refusal }, ct);
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync(ct);

                PairedDecision decision;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    var interactionId = root.TryGetProperty("interactionId", out var idElement) ? idElement.GetString() : null;

                    if (root.TryGetProperty("approved", out var approvedElement) &&
                        approvedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        decision = bridge.Answer(interactionId, approvedElement.GetBoolean());
                    else if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                        decision = bridge.AnswerText(interactionId, textElement.GetString());
                    else
                        decision = new PairedDecision(false, "请求体需要 approved 或 text");
                }
                catch (JsonException)
                {
                    decision = new PairedDecision(false, "请求体不是合法 JSON");
                }

                response.StatusCode = decision.Ok ? 200 : 409;
                await WriteJsonAsync(response, new { success = decision.Ok, message = decision.Message }, ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/notes/sync")
            {
                var bridge = NoteBridge;
                if (bridge is null)
                {
                    response.StatusCode = 503;
                    await WriteJsonAsync(response, new { success = false, error = "note_sync_unavailable" }, ct);
                    return;
                }

                var refusal = bridge.RejectReason(request.Headers["X-From-Device"], request.Headers["X-Device-Token"]);
                if (refusal is not null)
                {
                    Log($"Note sync rejected ({refusal}): {request.Headers["X-From-Device"] ?? "unknown"}");
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new { success = false, error = refusal }, ct);
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync(ct);

                var (ok, message, json) = bridge.Handle(body);
                if (!ok) Log("Note sync failed: " + message);

                response.StatusCode = ok ? 200 : 409;
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
            }
            else if (request.HttpMethod == "POST" && request.Url?.LocalPath == "/sync/config")
            {
                // Config sync — send personality + provider info to mobile
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var doc = JsonDocument.Parse(body);
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var fromDevice = request.Headers["X-From-Device"] ?? "unknown";

                var config = ConfigManager.Load();
                bool verified = !string.IsNullOrEmpty(config.Sync.PairingCode) &&
                                ConfigManager.VerifyPairingCode(code, config.Sync.PairingCode);

                if (verified)
                {
                    var syncJson = ConfigManager.ExportForSync();
                    Log($"Config synced to {fromDevice}");
                    var buffer = Encoding.UTF8.GetBytes(syncJson);
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(buffer, ct);
                }
                else
                {
                    response.StatusCode = 403;
                    var errBuffer = Encoding.UTF8.GetBytes("{\"error\":\"pairing required\"}");
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(errBuffer, ct);
                }
            }
            else
            {
                response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            Log($"Request error: {ex.Message}");
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task BroadcastAnnouncementAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = new
                {
                    deviceId = Environment.MachineName,
                    deviceName = _config.DeviceName,
                    endpoint = _localEndpoint,
                    app = "FairyAI"
                };
                var json = JsonSerializer.Serialize(info);
                var data = Encoding.UTF8.GetBytes(json);

                // Broadcast on common ports used by LocalSend
                var broadcastEndpoints = new[]
                {
                    new IPEndPoint(IPAddress.Broadcast, 53317),
                    new IPEndPoint(IPAddress.Broadcast, 53318)
                };

                foreach (var ep in broadcastEndpoints)
                {
                    await udp.SendAsync(data, data.Length, ep);
                }

                await Task.Delay(5000, ct); // Broadcast every 5 seconds
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(1000, ct); }
        }
    }

    public async Task SendCommandAsync(string command, string payload)
    {
        var msg = new CommandMessage
        {
            FromDeviceId = Environment.MachineName,
            Command = command,
            Payload = payload
        };
        var json = JsonSerializer.Serialize(msg);
        var data = new StringContent(json, Encoding.UTF8, "application/json");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var targetUrl = $"http://127.0.0.1:{_config.FileServerPort}/command";
            await client.PostAsync(targetUrl, data);
        }
        catch (Exception ex)
        {
            Log($"Send command error: {ex.Message}");
        }
    }

    public async Task<SyncDeviceInfo?> DiscoverDeviceAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetStringAsync($"http://127.0.0.1:{_config.FileServerPort}/announce");
            var info = JsonSerializer.Deserialize<JsonElement>(response);

            return new SyncDeviceInfo
            {
                DeviceId = info.GetProperty("deviceId").GetString() ?? "",
                DeviceName = info.GetProperty("deviceName").GetString() ?? "",
                Endpoint = info.GetProperty("endpoint").GetString() ?? "",
                Protocol = "http"
            };
        }
        catch { return null; }
    }

    public async Task<string> SendFileAsync(string filePath, string targetEndpoint)
    {
        if (!File.Exists(filePath))
            return "File not found";

        var fileName = Path.GetFileName(filePath);
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var content = new ByteArrayContent(fileBytes);
        content.Headers.Add("X-File-Name", fileName);
        content.Headers.Add("X-From-Device", Environment.MachineName);

        var response = await client.PostAsync($"{targetEndpoint}/send", content);
        return response.IsSuccessStatusCode ? "ok" : $"Error: {response.StatusCode}";
    }

    async Task IFileTransferService.StartServerAsync(int port)
    {
        await StartServerAsync(port);
    }

    Task IFileTransferService.StopServerAsync()
    {
        return StopAsync();
    }

    public async Task StartServerAsync(int port)
    {
        if (_isRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            // Security: bind localhost by default; LAN mode requires explicit enable
            if (_lanEnabled)
                _httpListener.Prefixes.Add($"http://+:{port}/");
            else
                _httpListener.Prefixes.Add($"http://localhost:{port}/");
            _httpListener.Start();
            _isRunning = true;
            _localEndpoint = $"http://{GetLocalIPAddress()}:{port}";

            Log($"LAN file server started on {_localEndpoint}");
            _ = ListenForRequestsAsync(_cts.Token);
            _ = BroadcastAnnouncementAsync(_cts.Token);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log($"LAN file server start error: {ex.Message}");
        }
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    public void Dispose()
    {
        StopAsync().Wait();
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] [LAN] {msg}\n"); } catch { }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken ct)
    {
        response.ContentType = "application/json; charset=utf-8";
        var buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await response.OutputStream.WriteAsync(buffer, ct);
    }

    private class CommandMessage
    {
        public string FromDeviceId { get; set; } = "";
        public string Command { get; set; } = "";
        public string Payload { get; set; } = "";
    }
}
