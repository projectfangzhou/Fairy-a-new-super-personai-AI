using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.Messaging;
using MyAiAssistant.Services.Notifications;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Mcp;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

namespace MyAiAssistant.Services.Dashboard;

public sealed record DashboardResponse(int StatusCode, string ContentType, string Body)
{
    public static DashboardResponse Json(string body, int status = 200) =>
        new(status, "application/json; charset=utf-8", body);
}

/// <summary>
/// Pure request handling for the control centre, kept away from the socket layer so the
/// routing, the token check and the state-machine rejections are all testable.
///
/// Control actions require a per-session token. Without it a local process — including an
/// agent that has shell access — could approve its own high-risk action by posting to
/// localhost, which would defeat the approval gate entirely.
/// </summary>
public sealed class DashboardApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AgentControlCenter _center;
    private readonly OperationAudit _audit;
    private readonly ISettingsStore? _settings;
    private readonly MessagingAccountStore? _accounts;
    private readonly MessagingAccountService? _accountService;
    private readonly FairyCodeSessionManager? _fairyCode;
    private readonly ILocalSendSettings? _localSend;
    private readonly ILocalSendPeers? _localSendPeers;
    private readonly IPairingControl? _pairing;
    private readonly ITransferControl? _transfer;
    private readonly INotificationSettings? _notificationSettings;
    private readonly NotificationCenter? _notificationCenter;
    private readonly IHomeAssistantControl? _homeAssistant;
    private readonly SystemNotificationRelay? _systemNotifications;
    private readonly ISummarySyncControl? _summarySync;
    private readonly IScreenSessionControl? _screenSession;
    private readonly IMcpStatus? _mcp;
    private readonly byte[] _token;
    private readonly IAppearanceControl? _appearance;

    public DashboardApi(AgentControlCenter center, OperationAudit audit, string controlToken,
        ISettingsStore? settings = null, MessagingAccountStore? accounts = null,
        MessagingAccountService? accountService = null, FairyCodeSessionManager? fairyCode = null,
        ILocalSendSettings? localSend = null, ILocalSendPeers? localSendPeers = null,
        IPairingControl? pairing = null, ITransferControl? transfer = null,
        INotificationSettings? notificationSettings = null, NotificationCenter? notificationCenter = null,
        IHomeAssistantControl? homeAssistant = null, SystemNotificationRelay? systemNotifications = null,
        ISummarySyncControl? summarySync = null, IScreenSessionControl? screenSession = null, IMcpStatus? mcp = null,
        IAppearanceControl? appearance = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        _token = Encoding.UTF8.GetBytes(controlToken);
        _settings = settings;
        _accounts = accounts;
        _accountService = accountService;
        _fairyCode = fairyCode;
        _localSend = localSend;
        _localSendPeers = localSendPeers;
        _pairing = pairing;
        _transfer = transfer;
        _notificationSettings = notificationSettings;
        _notificationCenter = notificationCenter;
        _homeAssistant = homeAssistant;
        _systemNotifications = systemNotifications;
        _summarySync = summarySync;
        _screenSession = screenSession;
        _mcp = mcp;
        _appearance = appearance;
    }

    public DashboardResponse Get(string path, string? token)
    {
        path = Normalize(path);

        // Audit rows, settings and the session probe all require the token. Loopback alone
        // is not a boundary: any local process, including an agent, is on it.
        if (RequiresToken(path) && !TokenMatches(token)) return Unauthorized();

        return path switch
        {
            "/api/session" => Session(),
            "/api/tasks" => Tasks(),
            "/api/pending" => Pending(),
            "/api/events" => Events(),
            "/api/resources" => Resources(),
            "/api/audit" => Audit(),
            "/api/settings" => ReadSettings(),
            "/api/appearance/theme" => ReadTheme(),
            "/api/platforms" => Platforms(),
            "/api/accounts" => Accounts(),
            "/api/fairycode" => FairyCodeSessions(),
            "/api/localsend" => LocalSendState(),
            "/api/pairing" => PairingStateResponse(),
            "/api/transfer" => TransferStateResponse(),
            "/api/notifications" => NotificationsState(),
            "/api/homeassistant" => HomeAssistantState(),
            "/api/summary-sync" => SummarySyncState(),
            "/api/screen" => ScreenSessionState(),
            "/api/mcp" => McpState(),
            _ => NotFound()
        };
    }

    public async Task<DashboardResponse> PostAsync(string path, string? token, string? body)
    {
        path = Normalize(path);
        if (RequiresToken(path) && !TokenMatches(token)) return Unauthorized();

        if (path == "/api/settings") return UpdateSettings(body);
        if (path == "/api/appearance/theme") return ApplyTheme(body);
        if (path == "/api/accounts") return AddAccount(body);
        if (path == "/api/fairycode/start") return await StartFairyCodeAsync(body).ConfigureAwait(false);
        if (path == "/api/fairycode/probe") return await ProbeFairyCodeAsync().ConfigureAwait(false);
        if (path == "/api/localsend") return ApplyLocalSend(body);
        if (path == "/api/localsend/send") return await SendLocalSendAsync(body).ConfigureAwait(false);
        if (path == "/api/pairing/settings") return ApplyPairingSettings(body);
        if (path == "/api/pairing/groups") return CreatePairingGroup(body);
        if (path == "/api/pairing/invites") return CreatePairingInvite(body);
        if (path == "/api/transfer/send") return await SendTransferAsync(body).ConfigureAwait(false);
        if (path == "/api/homeassistant/settings")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(_homeAssistant.ApplySettings(
                    ReadString(body, "serverUrl") ?? "", ReadString(body, "clientId"), ReadString(body, "redirectUri")));
        if (path == "/api/homeassistant/authorize")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(_homeAssistant.BeginAuthorization());
        if (path == "/api/homeassistant/complete")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(await _homeAssistant
                    .CompleteAuthorizationAsync(ReadString(body, "callback") ?? "").ConfigureAwait(false));
        if (path == "/api/homeassistant/token")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(await _homeAssistant
                    .UseAccessTokenAsync(ReadString(body, "token") ?? "").ConfigureAwait(false));
        if (path == "/api/homeassistant/connect")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(await _homeAssistant.ConnectAsync().ConfigureAwait(false));
        if (path == "/api/homeassistant/signout")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(_homeAssistant.SignOut());
        if (path == "/api/homeassistant/call")
            return _homeAssistant is null
                ? Unavailable(HomeAssistantDisabled)
                : HomeAssistantAction(await _homeAssistant.CallServiceAsync(
                    ReadString(body, "domain") ?? "",
                    ReadString(body, "service") ?? "",
                    ReadString(body, "entityId"),
                    ReadString(body, "data")).ConfigureAwait(false));

        if (path == "/api/mcp/start")
            return _mcp is null
                ? Unavailable("MCP 后端未启用")
                : McpAction(await _mcp.StartAsync().ConfigureAwait(false));
        if (path == "/api/mcp/stop")
            return _mcp is null
                ? Unavailable("MCP 后端未启用")
                : McpAction(await _mcp.StopAsync().ConfigureAwait(false));

        if (path == "/api/screen/pause")
            return _screenSession is null
                ? Unavailable("屏幕操作未启用")
                : ScreenSessionActionResponse(_screenSession.Pause());
        if (path == "/api/screen/resume")
            return _screenSession is null
                ? Unavailable("屏幕操作未启用")
                : ScreenSessionActionResponse(_screenSession.Resume());
        if (path == "/api/screen/stop")
            return _screenSession is null
                ? Unavailable("屏幕操作未启用")
                : ScreenSessionActionResponse(_screenSession.Stop());

        if (path == "/api/summary-sync/notes")
        {
            if (_summarySync is null) return Unavailable("摘要同步未启用");
            if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

                var enabled = ReadBool(document.RootElement, "enabled");
                if (enabled is null) return BadRequest("缺少 enabled");

                return SummarySyncAction(_summarySync.ApplyNotes(enabled.Value));
            }
            catch (JsonException)
            {
                return BadRequest("请求体不是合法 JSON");
            }
        }

        if (path == "/api/summary-sync/pair-control")
        {
            if (_summarySync is null) return Unavailable("摘要同步未启用");
            if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

                var enabled = ReadBool(document.RootElement, "enabled");
                if (enabled is null) return BadRequest("缺少 enabled");

                return SummarySyncAction(_summarySync.ApplyPairControl(enabled.Value));
            }
            catch (JsonException)
            {
                return BadRequest("请求体不是合法 JSON");
            }
        }

        if (path == "/api/summary-sync/settings")
        {
            if (_summarySync is null) return Unavailable("摘要同步未启用");
            if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

                var enabled = ReadBool(document.RootElement, "enabled");
                if (enabled is null) return BadRequest("缺少 enabled");

                return SummarySyncAction(_summarySync.Apply(enabled.Value));
            }
            catch (JsonException)
            {
                return BadRequest("请求体不是合法 JSON");
            }
        }

        if (path == "/api/notifications/settings") return ApplyNotificationSettings(body);
        if (path == "/api/notifications/read") return MarkNotificationRead(body);
        if (path == "/api/notifications/read-all")
            return _notificationCenter is null
                ? Unavailable("通知未启用")
                : DashboardResponse.Json(JsonSerializer.Serialize(
                    new { ok = true, message = $"已标记 {_notificationCenter.MarkAllRead()} 条" }, JsonOptions));
        if (path == "/api/notifications/clear")
            return _notificationCenter is null
                ? Unavailable("通知未启用")
                : DashboardResponse.Json(JsonSerializer.Serialize(
                    new { ok = true, message = $"已清空 {_notificationCenter.Clear()} 条" }, JsonOptions));

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "summary-sync" && segments[2] == "devices")
        {
            if (segments[4] == "revoke")
                return _summarySync is null
                    ? Unavailable("摘要同步未启用")
                    : SummarySyncAction(_summarySync.Revoke(Uri.UnescapeDataString(segments[3])));
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "pairing" && segments[2] == "groups")
        {
            return segments[4] switch
            {
                "active" => PairingAction(() => _pairing!.SetActiveGroup(segments[3])),
                "rotate" => PairingAction(() => _pairing!.RotateKey(segments[3])),
                _ => NotFound()
            };
        }

        if (segments.Length == 5 && segments[0] == "api" && segments[1] == "pairing" && segments[2] == "invites" && segments[4] == "cancel")
            return PairingAction(() => _pairing!.CancelInvite(segments[3]));

        if (segments.Length == 7 && segments[0] == "api" && segments[1] == "pairing" && segments[2] == "groups" &&
            segments[4] == "members")
        {
            var groupId = segments[3];
            var deviceId = segments[5];
            var capability = ReadString(body, "capability") ?? string.Empty;
            return segments[6] switch
            {
                "grant" => PairingAction(() => _pairing!.Grant(groupId, deviceId, capability)),
                "withdraw" => PairingAction(() => _pairing!.Withdraw(groupId, deviceId, capability)),
                "remove" => PairingAction(() => _pairing!.RemoveMember(groupId, deviceId)),
                _ => NotFound()
            };
        }

        if (segments.Length == 5 && segments[1] == "fairycode" && segments[2] == "sessions" && segments[4] == "stop")
            return Action(_fairyCode?.Stop(segments[3]) == true, "任务已停止", "未找到可停止的 Fairy Code 任务");

        if (segments.Length == 4 && segments[1] == "tasks")
        {
            var taskId = segments[2];
            return segments[3] switch
            {
                "pause" => Action(_center.RequestPause(taskId), "任务已请求暂停", "当前状态不允许暂停"),
                "resume" => Action(_center.RequestResume(taskId), "任务已继续", "当前状态不允许继续"),
                "stop" => Action(_center.RequestStop(taskId), "任务已停止", "任务已结束"),
                _ => NotFound()
            };
        }

        if (segments.Length == 4 && segments[1] == "interactions" && segments[3] == "answer")
            return Answer(segments[2], body);

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "accounts")
        {
            return segments[3] switch
            {
                "probe" => await ProbeAccountAsync(segments[2]).ConfigureAwait(false),
                "remove" => RemoveAccount(segments[2]),
                _ => NotFound()
            };
        }

        return NotFound();
    }

    /// <summary>Synchronous wrapper kept for callers and tests that do not await handlers.</summary>
    public DashboardResponse Post(string path, string? token, string? body) =>
        PostAsync(path, token, body).GetAwaiter().GetResult();
    public bool TokenMatches(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var provided = Encoding.UTF8.GetBytes(token);
        return provided.Length == _token.Length &&
               CryptographicOperations.FixedTimeEquals(provided, _token);
    }

    private static bool RequiresToken(string path) =>
        // Everything under /api/ needs the token; only the login page itself is public,
        // and it renders no data. Task titles and approval prompts are personal content.
        path.StartsWith("/api/", StringComparison.Ordinal);

    private DashboardResponse Session() =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok = true }, JsonOptions));

    private DashboardResponse ReadSettings()
    {
        if (_settings is null) return Unavailable("设置存储不可用");
        var snapshot = _settings.Read();
        return DashboardResponse.Json(JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private DashboardResponse UpdateSettings(string? body)
    {
        if (_settings is null) return Unavailable("设置存储不可用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        SettingsUpdate update;
        var touchedCredentials = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

            update = new SettingsUpdate(
                ReadEndpoint(root, "llm"),
                ReadEndpoint(root, "fallbackLlm"),
                ReadEndpoint(root, "asr"),
                ReadEndpoint(root, "tts"),
                ReadEndpoint(root, "vision"),
                ReadText(root, "personalityName"),
                ReadText(root, "systemPrompt"),
                ReadText(root, "wakeWord"));

            foreach (var endpoint in new[] { update.Llm, update.FallbackLlm, update.Asr, update.Tts, update.Vision })
            {
                if (endpoint is null) continue;
                if (!string.IsNullOrWhiteSpace(endpoint.ApiKey)) touchedCredentials = true;
                if (!string.IsNullOrWhiteSpace(endpoint.BaseUrl) && !IsHttpUrl(endpoint.BaseUrl))
                    return BadRequest($"BaseUrl 必须是 http 或 https 地址：{endpoint.BaseUrl}");
            }
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (update is { Llm: null, FallbackLlm: null, Asr: null, Tts: null, Vision: null,
                PersonalityName: null, SystemPrompt: null, WakeWord: null })
            return BadRequest("没有需要修改的字段");

        _settings.Update(update);

        // The user typed this themselves behind the session token, so it is not routed back
        // through the approval gate; it is still recorded, because credential writes are
        // exactly what an audit trail is for.
        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "settings.update",
            touchedCredentials ? RiskCategory.ReadWriteCredentials : RiskCategory.None,
            "控制中心修改设置",
            null,
            "allowed",
            touchedCredentials ? "用户填写了新的密钥" : "用户修改了非密钥字段",
            "user",
            0));

        return ReadSettings();
    }

    private static EndpointUpdate? ReadEndpoint(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return null;

        var update = new EndpointUpdate(
            ReadText(element, "provider"),
            ReadText(element, "baseUrl"),
            ReadText(element, "model"),
            ReadText(element, "voice"),
            ReadText(element, "apiKey"));

        // A JSON null or "" means "unchanged", so an untouched form never clears a value.
        return update is { Provider: null, BaseUrl: null, Model: null, Voice: null, ApiKey: null }
            ? null
            : update;
    }

    private DashboardResponse PairingStateResponse()
    {
        if (_pairing is null) return Unavailable("配对未启用");
        var view = _pairing.Read();

        // Only what a member is: no key material is in any of these records, which is why the
        // group key cannot leak through this endpoint even by accident.
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = view.Enabled,
            listening = view.Listening,
            port = view.Port,
            displayName = view.DisplayName,
            deviceId = view.DeviceId,
            shortDeviceId = view.ShortDeviceId,
            activeGroupId = view.ActiveGroupId,
            listenError = view.ListenError,
            loadError = view.LoadError,
            capabilities = Enum.GetValues<PairingCapability>().Select(capability => new
            {
                name = capability.ToString(),
                text = capability.Describe(),
                requiresApproval = capability.RequiresPerCallApproval()
            }),
            groups = view.Groups.Select(group => new
            {
                groupId = group.GroupId,
                shortId = group.ShortId,
                name = group.Name,
                createdAt = group.CreatedAt,
                keyEpoch = group.KeyEpoch,
                keyFingerprint = group.KeyFingerprint,
                active = group.GroupId == view.ActiveGroupId,
                members = group.Members.Select(member => new
                {
                    deviceId = member.DeviceId,
                    shortId = member.ShortId,
                    displayName = member.DisplayName,
                    joinedAt = member.JoinedAt,
                    capabilities = member.Capabilities.Select(capability => capability.ToString())
                })
            }),
            invites = view.Invites.Select(invite => new
            {
                inviteId = invite.InviteId,
                groupId = invite.GroupId,
                expiresAt = invite.ExpiresAt,
                remainingAttempts = invite.RemainingAttempts
            })
        }, JsonOptions));
    }

    private DashboardResponse ApplyPairingSettings(string? body)
    {
        if (_pairing is null) return Unavailable("配对未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        bool? enabled = null;
        int? port = null;
        string? displayName = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

            if (root.TryGetProperty("enabled", out var enabledElement) &&
                enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                enabled = enabledElement.GetBoolean();
            }

            if (root.TryGetProperty("port", out var portElement) && portElement.ValueKind == JsonValueKind.Number &&
                portElement.TryGetInt32(out var portValue))
            {
                port = portValue;
            }

            displayName = ReadText(root, "displayName");
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (enabled is null && port is null && displayName is null)
            return BadRequest("没有任何要修改的设置");

        var result = _pairing.Apply(enabled, port, displayName);
        return PairingResult(result, showCode: false);
    }

    private DashboardResponse CreatePairingGroup(string? body)
    {
        if (_pairing is null) return Unavailable("配对未启用");
        var name = ReadString(body, "name");
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("缺少设备组名称");
        return PairingResult(_pairing.CreateGroup(name), showCode: false);
    }

    private DashboardResponse CreatePairingInvite(string? body)
    {
        if (_pairing is null) return Unavailable("配对未启用");
        var groupId = ReadString(body, "groupId");
        return PairingResult(_pairing.CreateInvite(groupId), showCode: true);
    }

    private DashboardResponse PairingAction(Func<PairingActionResult> action)
    {
        if (_pairing is null) return Unavailable("配对未启用");
        return PairingResult(action(), showCode: false);
    }

    /// <summary>
    /// <paramref name="showCode"/> is only ever true for the call that created a code: it is the
    /// single response that carries it, and nothing stores it afterwards.
    /// </summary>
    private static DashboardResponse PairingResult(PairingActionResult result, bool showCode)
    {
        // The code field exists only when a code was actually issued: a refusal that carried an
        // empty "code" property would suggest one had been created.
        var payload = showCode && result.Code is not null
            ? JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message, code = result.Code }, JsonOptions)
            : JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message }, JsonOptions);

        return DashboardResponse.Json(payload, result.Ok ? 200 : 409);
    }

    private DashboardResponse TransferStateResponse()
    {
        if (_transfer is null) return Unavailable("传输未启用");
        var view = _transfer.Read();

        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = view.Enabled,
            error = view.Error,
            hostDeviceId = view.HostDeviceId,
            destinationRoot = view.DestinationRoot,
            peers = view.Peers.Select(peer => new
            {
                deviceId = peer.DeviceId,
                displayName = peer.DisplayName,
                groupId = peer.GroupId,
                groupName = peer.GroupName
            }),
            jobs = view.Jobs.Select(job => new
            {
                id = job.Id,
                peer = job.Peer,
                direction = job.Direction,
                state = job.State,
                bytes = job.Bytes,
                error = job.Error,
                startedAt = job.StartedAt
            }),
            received = view.Received.Select(file => new
            {
                name = file.Name,
                size = file.Size,
                completed = file.Completed,
                savedPath = file.SavedPath
            })
        }, JsonOptions));
    }

    private async Task<DashboardResponse> SendTransferAsync(string? body)
    {
        if (_transfer is null) return Unavailable("传输未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        string? deviceId = null;
        var paths = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

            deviceId = ReadText(root, "deviceId");
            if (root.TryGetProperty("paths", out var pathsElement) && pathsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pathsElement.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                        paths.Add(entry.GetString()!.Trim());
            }
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest("没有指定接收设备");
        if (paths.Count == 0) return BadRequest("请填写要发送的文件路径");

        var result = await _transfer.SendAsync(deviceId, paths).ConfigureAwait(false);
        return DashboardResponse.Json(
            JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message }, JsonOptions),
            result.Ok ? 200 : 409);
    }

    private DashboardResponse NotificationsState()
    {
        if (_notificationCenter is null || _notificationSettings is null) return Unavailable("通知未启用");

        var preferences = _notificationSettings.Read();
        var items = _notificationCenter.Recent(100);

        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = preferences.Enabled,
            unread = _notificationCenter.UnreadCount,
            quietHoursEnabled = preferences.QuietHoursEnabled,
            quietHoursStart = preferences.QuietHoursStart,
            quietHoursEnd = preferences.QuietHoursEnd,
            maxEntries = preferences.MaxEntries,
            systemEnabled = preferences.SystemEnabled,
            mirrorKeyEvents = preferences.MirrorKeyEvents,
            systemDetails = preferences.SystemDetails,
            systemChannel = _systemNotifications is null
                ? null
                : new
                {
                    platform = _systemNotifications.Channel.Platform,
                    available = _systemNotifications.Channel.IsAvailable,
                    error = _systemNotifications.Channel.LastError,
                    delivered = _systemNotifications.Delivered,
                    suppressed = _systemNotifications.Suppressed,
                    lastSkipReason = _systemNotifications.LastSkipReason
                },
            kinds = new
            {
                taskCompleted = preferences.TaskCompleted,
                taskFailed = preferences.TaskFailed,
                approvalPending = preferences.ApprovalPending,
                questionPending = preferences.QuestionPending,
                transferIncoming = preferences.TransferIncoming
            },
            items = items.Select(entry => new
            {
                id = entry.Id,
                kind = entry.Kind.ToString(),
                severity = entry.Severity.ToString(),
                title = entry.Title,
                body = entry.Body,
                taskId = entry.TaskId,
                targetId = entry.TargetId,
                createdAt = entry.CreatedAt,
                unread = entry.IsUnread,
                actions = entry.Actions.Select(action => new { id = action.Id, label = action.Label })
            })
        }, JsonOptions));
    }

    private DashboardResponse ApplyNotificationSettings(string? body)
    {
        if (_notificationSettings is null) return Unavailable("通知未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        var current = _notificationSettings.Read();
        var updated = current;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

            updated = current with
            {
                Enabled = ReadBool(root, "enabled") ?? current.Enabled,
                TaskCompleted = ReadBool(root, "taskCompleted") ?? current.TaskCompleted,
                TaskFailed = ReadBool(root, "taskFailed") ?? current.TaskFailed,
                ApprovalPending = ReadBool(root, "approvalPending") ?? current.ApprovalPending,
                QuestionPending = ReadBool(root, "questionPending") ?? current.QuestionPending,
                TransferIncoming = ReadBool(root, "transferIncoming") ?? current.TransferIncoming,
                QuietHoursEnabled = ReadBool(root, "quietHoursEnabled") ?? current.QuietHoursEnabled,
                QuietHoursStart = ReadInt(root, "quietHoursStart") ?? current.QuietHoursStart,
                QuietHoursEnd = ReadInt(root, "quietHoursEnd") ?? current.QuietHoursEnd,
                MaxEntries = ReadInt(root, "maxEntries") ?? current.MaxEntries,
                SystemEnabled = ReadBool(root, "systemEnabled") ?? current.SystemEnabled,
                MirrorKeyEvents = ReadBool(root, "mirrorKeyEvents") ?? current.MirrorKeyEvents,
                SystemDetails = ReadBool(root, "systemDetails") ?? current.SystemDetails
            };
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (updated == current) return BadRequest("没有任何要修改的设置");

        var result = _notificationSettings.Apply(updated);
        return DashboardResponse.Json(
            JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message }, JsonOptions),
            result.Ok ? 200 : 409);
    }

    private DashboardResponse MarkNotificationRead(string? body)
    {
        if (_notificationCenter is null) return Unavailable("通知未启用");
        var id = ReadString(body, "id");
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("缺少通知编号");

        return _notificationCenter.MarkRead(id)
            ? DashboardResponse.Json(JsonSerializer.Serialize(new { ok = true, message = "已标记" }, JsonOptions))
            : DashboardResponse.Json(JsonSerializer.Serialize(new { ok = false, message = "通知不存在" }, JsonOptions), 404);
    }

    private static bool? ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var value)
            ? value
            : null;

    private static string? ReadString(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadText(document.RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private DashboardResponse Platforms()
    {
        // Built-in adapters are included so the catalogue matches what can actually be added.
        var registry = new MessagingPlatformRegistry(new IMessagingAdapter[]
        {
            new TelegramAdapter(null),
            new QQAdapter(),
            new QQOfficialAdapter(() => null, () => null),
            new WebChatAdapter()
        });
        var platforms = registry.Platforms.Select(p => new
        {
            kind = p.Kind.ToString(),
            name = p.DisplayName,
            implemented = p.State == ImplementationState.Implemented,
            requirement = p.Requirement,
            fields = MessagingAccountService.RequiredFields(p.Kind).Select(f => new
            {
                key = f.Key,
                label = f.Label,
                secret = f.IsSecret
            })
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { platforms }, JsonOptions));
    }

    private DashboardResponse Accounts()
    {
        if (_accounts is null) return Unavailable("账号存储不可用");
        var accounts = _accounts.List().Select(a => new
        {
            id = a.Id,
            platform = a.Platform.ToString(),
            platformName = a.PlatformName,
            displayName = a.DisplayName,
            enabled = a.Enabled,
            filledFields = a.FilledFields,
            missingFields = a.MissingFields,
            implemented = a.Implemented
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { accounts }, JsonOptions));
    }

    /// <summary>
    /// Reports the theme the desktop app is currently on, so the control centre opens on the
    /// same one instead of its own browser-local default.
    /// </summary>
    private DashboardResponse ReadTheme()
    {
        if (_appearance is null) return Unavailable("外观设置不可用");
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            theme = _appearance.CurrentTheme,
        }, JsonOptions));
    }

    /// <summary>
    /// Applies an appearance theme. The control centre runs in a WebView2, so its own theme
    /// switcher only changed the page; going through the app keeps the two in step — the
    /// native windows and the dashboard end up on the same theme.
    /// </summary>
    private DashboardResponse ApplyTheme(string? body)
    {
        if (_appearance is null) return Unavailable("外观设置不可用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        string name;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");
            if (!root.TryGetProperty("theme", out var themeElement) ||
                themeElement.ValueKind != JsonValueKind.String)
                return BadRequest("缺少 theme 字段");

            name = themeElement.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (!_appearance.ApplyTheme(name))
            return BadRequest($"未知主题：{name}");

        return DashboardResponse.Json(JsonSerializer.Serialize(new { theme = name }, JsonOptions));
    }

    private DashboardResponse AddAccount(string? body)
    {
        if (_accounts is null) return Unavailable("账号存储不可用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        string platformText, displayName;
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            platformText = ReadText(root, "platform") ?? "";
            displayName = ReadText(root, "displayName") ?? "";

            if (root.TryGetProperty("credentials", out var credentialsElement) &&
                credentialsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in credentialsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) credentials[property.Name] = value.Trim();
                }
            }

            // Older clients post a single token field.
            var legacyToken = ReadText(root, "token");
            if (legacyToken is not null && !credentials.ContainsKey("token"))
                credentials["token"] = legacyToken;
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (!Enum.TryParse<PlatformKind>(platformText, ignoreCase: true, out var platform))
            return BadRequest("未知平台");

        var (ok, message, id) = _accounts.Add(platform, displayName, credentials);
        return DashboardResponse.Json(JsonSerializer.Serialize(new { ok, message, id }, JsonOptions), ok ? 200 : 409);
    }

    private async Task<DashboardResponse> ProbeAccountAsync(string accountId)
    {
        if (_accountService is null) return Unavailable("账号服务不可用");
        var result = await _accountService.ProbeAsync(accountId).ConfigureAwait(false);
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            state = result.State.ToString(),
            ok = result.IsReady,
            message = result.Message
        }, JsonOptions), result.State == PlatformProbeState.Failed ? 502 : 200);
    }

    private DashboardResponse RemoveAccount(string accountId)
    {
        if (_accounts is null) return Unavailable("账号存储不可用");
        var removed = _accounts.Remove(accountId);
        return Action(removed, "已删除账号", "账号不存在");
    }

    private DashboardResponse FairyCodeSessions()
    {
        if (_fairyCode is null) return Unavailable("Fairy Code 未启用");
        var sessions = _fairyCode.Sessions().Select(s => new
        {
            taskId = s.TaskId,
            title = s.Title,
            workspace = s.Workspace,
            engine = s.Engine,
            model = s.Model,
            running = s.IsRunning,
            startedAt = s.StartedAt,
            lastError = s.LastError
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { sessions }, JsonOptions));
    }

    private async Task<DashboardResponse> ProbeFairyCodeAsync()
    {
        if (_fairyCode is null) return Unavailable("Fairy Code 未启用");
        var availability = await _fairyCode.ProbeEngineAsync().ConfigureAwait(false);
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            state = availability.State.ToString(),
            ready = availability.IsReady,
            detail = availability.Detail,
            version = availability.Version
        }, JsonOptions));
    }

    private async Task<DashboardResponse> StartFairyCodeAsync(string? body)
    {
        if (_fairyCode is null) return Unavailable("Fairy Code 未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        string? prompt = null;
        string? workspace = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");
            prompt = ReadText(root, "prompt");
            workspace = ReadText(root, "workspace");
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (prompt is null) return BadRequest("缺少任务描述");

        var result = await _fairyCode.StartAsync(prompt, workspace).ConfigureAwait(false);
        return DashboardResponse.Json(
            JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message, taskId = result.TaskId }, JsonOptions),
            result.Ok ? 200 : 409);
    }

    private DashboardResponse LocalSendState()
    {
        if (_localSend is null) return Unavailable("局域网接收未启用");
        var settings = _localSend.Read();
        var peers = _localSendPeers?.Peers() ?? [];
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = settings.Enabled,
            running = settings.Running,
            port = settings.Port,
            alias = settings.Alias,
            saveDirectory = settings.SaveDirectory,
            discoveryRunning = _localSendPeers?.DiscoveryRunning == true,
            peers = peers.Select(peer => new
            {
                fingerprint = peer.Fingerprint,
                alias = peer.Alias,
                deviceType = peer.DeviceType,
                endpoint = peer.Endpoint,
                lastSeen = peer.LastSeen
            })
        }, JsonOptions));
    }

    private async Task<DashboardResponse> SendLocalSendAsync(string? body)
    {
        if (_localSendPeers is null) return Unavailable("局域网发送未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        string? fingerprint = null;
        var paths = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");
            fingerprint = ReadText(root, "fingerprint");
            if (root.TryGetProperty("paths", out var pathsElement) && pathsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pathsElement.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                        paths.Add(entry.GetString()!.Trim());
            }
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (fingerprint is null) return BadRequest("缺少目标设备");
        if (paths.Count == 0) return BadRequest("缺少文件路径");

        var result = await _localSendPeers.SendAsync(fingerprint, paths, CancellationToken.None)
            .ConfigureAwait(false);
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            ok = result.Ok,
            message = result.Message,
            uploadedFiles = result.UploadedFiles
        }, JsonOptions), result.Ok ? 200 : 409);
    }

    private DashboardResponse ApplyLocalSend(string? body)
    {
        if (_localSend is null) return Unavailable("局域网接收未启用");
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");

        bool? enabled = null;
        string? alias = null;
        string? saveDirectory = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("请求体必须是对象");

            if (root.TryGetProperty("enabled", out var enabledValue))
            {
                if (enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return BadRequest("enabled 必须是布尔值");
                enabled = enabledValue.GetBoolean();
            }
            alias = ReadText(root, "alias");
            saveDirectory = ReadText(root, "saveDirectory");
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }

        if (enabled is null && alias is null && saveDirectory is null)
            return BadRequest("没有需要修改的字段");

        var (ok, message) = _localSend.Apply(enabled, alias, saveDirectory);

        // Turning the listener on is an outside-facing change, so it is written to the audit
        // trail even though the user did it themselves.
        _audit.Record(new OperationRecord(
            DateTimeOffset.UtcNow,
            "localsend.settings",
            RiskCategory.None,
            "修改局域网接收设置",
            null,
            ok ? "allowed" : "failed",
            ok ? message : message,
            "user",
            0));

        return DashboardResponse.Json(
            JsonSerializer.Serialize(new { ok, message }, JsonOptions), ok ? 200 : 409);
    }

    private DashboardResponse Tasks()
    {        var tasks = _center.Tasks().Select(t => new
        {
            id = t.Id,
            title = t.Title,
            state = t.State.ToString(),
            pauseRequested = t.PauseRequested,
            engine = t.Engine,
            model = t.Model,
            step = t.Step,
            totalSteps = t.TotalSteps,
            stepLabel = t.StepLabel,
            createdAt = t.CreatedAt,
            updatedAt = t.UpdatedAt,
            error = t.Error,
            canPause = t.CanPause,
            canResume = t.CanResume,
            canStop = t.CanStop
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { tasks }, JsonOptions));
    }

    private DashboardResponse Pending()
    {
        var pending = _center.Pending().Select(p => new
        {
            id = p.Id,
            taskId = p.TaskId,
            kind = p.Kind.ToString(),
            prompt = p.Prompt,
            risk = p.Risk?.ToString(),
            riskText = p.Risk?.Describe(),
            createdAt = p.CreatedAt
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { pending }, JsonOptions));
    }

    private DashboardResponse Events()
    {
        var events = _center.Events(100).Select(e => new
        {
            at = e.At,
            type = e.Type.ToString(),
            taskId = e.TaskId,
            detail = e.Detail
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { events }, JsonOptions));
    }

    private DashboardResponse Resources()
    {
        var usage = ResourceUsage.Sample();
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            workingSetBytes = usage.WorkingSetBytes,
            privateBytes = usage.PrivateBytes,
            threadCount = usage.ThreadCount,
            uptimeSeconds = (long)usage.Uptime.TotalSeconds,
            sampledAt = usage.At
        }, JsonOptions));
    }

    private DashboardResponse Audit()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var records = _audit.Query(since, DateTimeOffset.UtcNow).TakeLast(100).Select(r => new
        {
            at = r.TimestampUtc,
            actionId = r.ActionId,
            category = r.Category.ToString(),
            categoryText = r.Category.Describe(),
            summary = r.Summary,
            target = r.Target,
            decision = r.Decision,
            reason = r.Reason,
            reviewer = r.Reviewer
        });
        return DashboardResponse.Json(JsonSerializer.Serialize(new { records }, JsonOptions));
    }

    private DashboardResponse Answer(string interactionId, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return BadRequest("缺少请求体");
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("approved", out var approved) && approved.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return Action(_center.Answer(interactionId, approved.GetBoolean()), "已回答", "该待决项已不存在");

            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return Action(_center.AnswerText(interactionId, text.GetString() ?? ""), "已回答", "该待决项已不存在");

            return BadRequest("请求体需要 approved 或 text 字段");
        }
        catch (JsonException)
        {
            return BadRequest("请求体不是合法 JSON");
        }
    }

    private DashboardResponse McpState()
    {
        if (_mcp is null) return Unavailable("MCP 后端未启用");

        var view = _mcp.Read();
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = view.Enabled,
            connected = view.Connected,
            serverCommand = view.ServerCommand,
            serverName = view.ServerName,
            serverVersion = view.ServerVersion,
            toolCount = view.ToolCount,
            toolNames = view.ToolNames,
            error = view.Error
        }, JsonOptions));
    }

    private static DashboardResponse McpAction(McpActionResult result) =>
        DashboardResponse.Json(JsonSerializer.Serialize(
            new { ok = result.Ok, message = result.Message }, JsonOptions),
            result.Ok ? 200 : 409);

    private DashboardResponse ScreenSessionState()
    {
        if (_screenSession is null) return Unavailable("屏幕操作未启用");

        var view = _screenSession.Read();
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            state = view.State,
            pausePending = view.PausePending,
            inFlight = view.InFlight,
            completed = view.Completed,
            refused = view.Refused,
            lastRefusal = view.LastRefusal,
            supported = view.Supported,
            platform = view.Platform,
            unsupportedReason = view.UnsupportedReason
        }, JsonOptions));
    }

    private static DashboardResponse ScreenSessionActionResponse(ScreenSessionAction result) =>
        DashboardResponse.Json(JsonSerializer.Serialize(
            new { ok = result.Ok, message = result.Message }, JsonOptions),
            result.Ok ? 200 : 409);

    private DashboardResponse SummarySyncState()
    {
        if (_summarySync is null) return Unavailable("摘要同步未启用");

        var view = _summarySync.Read();
        return DashboardResponse.Json(JsonSerializer.Serialize(new
        {
            enabled = view.Enabled,
            pairControlEnabled = view.PairControlEnabled,
            notesSyncEnabled = view.NotesSyncEnabled,
            pairedDeviceCount = view.PairedDeviceCount,
            lastError = view.LastError,
            devices = view.Devices.Select(device => new { deviceName = device.DeviceName })
        }, JsonOptions));
    }

    private static DashboardResponse SummarySyncAction(SummarySyncActionResult result) =>
        DashboardResponse.Json(JsonSerializer.Serialize(
            new { ok = result.Ok, message = result.Message }, JsonOptions),
            result.Ok ? 200 : 409);

    private const string HomeAssistantDisabled = "Home Assistant 未启用";

    private DashboardResponse HomeAssistantState()
    {
        if (_homeAssistant is null) return Unavailable(HomeAssistantDisabled);
        return DashboardResponse.Json(JsonSerializer.Serialize(_homeAssistant.Read(), JsonOptions));
    }

    private static DashboardResponse HomeAssistantAction(HomeAssistantActionResult result) =>
        DashboardResponse.Json(JsonSerializer.Serialize(
            new { ok = result.Ok, message = result.Message, authorizeUrl = result.AuthorizeUrl }, JsonOptions),
            result.Ok ? 200 : 409);

    private static DashboardResponse Action(bool ok, string success, string failure) =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok, message = ok ? success : failure }, JsonOptions), ok ? 200 : 409);

    private static DashboardResponse Unauthorized() =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok = false, message = "缺少或无效的控制令牌" }), 401);

    private static DashboardResponse BadRequest(string message) =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok = false, message }), 400);

    private static DashboardResponse Unavailable(string message) =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok = false, message }), 503);

    private static DashboardResponse NotFound() =>
        DashboardResponse.Json(JsonSerializer.Serialize(new { ok = false, message = "未知接口" }), 404);

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}