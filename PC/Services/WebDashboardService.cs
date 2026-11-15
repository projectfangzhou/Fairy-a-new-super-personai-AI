using System.IO;
using System.Security.Cryptography;
using System.Text;
using MyAiAssistant.Services.AgentControl;
using MyAiAssistant.Services.FairyCode;
using MyAiAssistant.Services.Notifications;
using MyAiAssistant.Services.Http;
using MyAiAssistant.Services.Messaging;
using MyAiAssistant.Services.Mcp;
using MyAiAssistant.Services.Permissions;
using MyAiAssistant.Services.Screen;

namespace MyAiAssistant.Services;

/// <summary>
/// Loopback control centre: the same state the in-app window reads, rendered for a browser.
/// It only serves real task, resource and audit data — the previous implementation returned
/// hard-coded sample numbers, which is exactly the kind of placeholder the project forbids.
/// </summary>
public sealed class WebDashboardService
{
    private readonly Dashboard.DashboardApi _api;
    private readonly int _port;
    private readonly LocalHttpServer _server;
    private bool _running;

    public WebDashboardService(AgentControlCenter center, OperationAudit audit, Dashboard.ISettingsStore settings,
        MessagingAccountStore? accounts = null, MessagingAccountService? accountService = null,
        FairyCodeSessionManager? fairyCode = null, ILocalSendSettings? localSend = null, ILocalSendPeers? localSendPeers = null,
        IPairingControl? pairing = null, ITransferControl? transfer = null,
        INotificationSettings? notificationSettings = null, NotificationCenter? notificationCenter = null,
        IHomeAssistantControl? homeAssistant = null, SystemNotificationRelay? systemNotifications = null,
        ISummarySyncControl? summarySync = null, IScreenSessionControl? screenSession = null,
        IMcpStatus? mcp = null, int port = 8080)
    {
        ArgumentNullException.ThrowIfNull(center);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(settings);
        // Generated per session and never written to disk, so a local process cannot read
        // it to approve an action on the user's behalf.
        ControlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _api = new Dashboard.DashboardApi(center, audit, ControlToken, settings, accounts, accountService, fairyCode, localSend, localSendPeers, pairing, transfer, notificationSettings, notificationCenter, homeAssistant, systemNotifications, summarySync, screenSession, mcp);
        _port = port;
        _server = new LocalHttpServer(HandleAsync);
    }

    public string ControlToken { get; }

    public bool IsRunning => _running;

    /// <summary>
    /// Bound to loopback explicitly. The transport handles one request per connection on
    /// 127.0.0.1; it never listens on a real interface, so a LAN address cannot reach it even
    /// with a forged Host header.
    /// </summary>
    public string Url => $"http://127.0.0.1:{_server.Port}/";

    public string? LastError => _server.LastError;

    public Task<bool> StartAsync()
    {
        _running = _server.Start(_port);
        return Task.FromResult(_running);
    }

    private async Task<HttpResponseData> HandleAsync(HttpRequestData request)
    {
        Dashboard.DashboardResponse response;
        try
        {
            var path = request.Path;
            var token = request.Headers.GetValueOrDefault("X-Fairy-Token");

            if (path is "/" or "/index.html")
            {
                response = new(200, "text/html; charset=utf-8", Page);
            }
            else if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                response = await _api.PostAsync(path, token, request.Body);
            }
            else
            {
                response = _api.Get(path, token);
            }
        }
        catch (Exception ex)
        {
            response = new(500, "application/json; charset=utf-8",
                $"{{\"ok\":false,\"message\":\"{ex.GetType().Name}\"}}");
        }

        return new HttpResponseData(response.StatusCode, response.Body, response.ContentType,
            new Dictionary<string, string>
            {
                // No framing and no sniffing: this page carries live control state.
                ["X-Content-Type-Options"] = "nosniff",
                ["X-Frame-Options"] = "DENY"
            });
    }

    public void Stop()
    {
        _running = false;
        _server.Stop();
    }

    private const string Page = """
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>仙灵 · Agent 控制中心</title>
<style>
:root{--ink:#1f2430;--sub:#6b6880;--line:#e3e1ef;--accent:#7b68ee;--bg:#fbfbfe;--card:#fff;--warn:#b4453c}
*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--bg);color:var(--ink);
 font:14px/1.6 -apple-system,'PingFang SC','Microsoft YaHei',sans-serif}
h1{font-size:18px;font-weight:500;margin:0 0 4px}
h2{font-size:13px;font-weight:500;margin:0 0 12px;color:var(--sub);letter-spacing:.02em}
h3{font-size:12px;font-weight:500;margin:14px 0 6px;color:var(--sub)}
.sub{color:var(--sub);font-size:12px;margin:0 0 20px}
section{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:16px;margin-bottom:16px}
table{width:100%;border-collapse:collapse;font-size:13px}
th{text-align:left;font-weight:400;color:var(--sub);font-size:12px;padding:6px 8px;border-bottom:1px solid var(--line)}
td{padding:8px;border-bottom:1px solid var(--line);vertical-align:top}
tr:last-child td{border-bottom:none}
button{font:inherit;font-size:12px;padding:5px 12px;border-radius:6px;border:1px solid var(--line);
 background:#fff;color:var(--ink);cursor:pointer;margin-right:6px}
button:hover:not(:disabled){border-color:var(--accent);color:var(--accent)}
button:disabled{opacity:.38;cursor:not-allowed}
button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
button.danger:hover:not(:disabled){border-color:var(--warn);color:var(--warn)}
input,textarea{font:inherit;font-size:13px;padding:5px 8px;border:1px solid var(--line);border-radius:6px;width:100%}
textarea{resize:vertical}
label{display:block;font-size:11px;color:var(--sub);margin:8px 0 3px}
.grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px}
.empty{color:var(--sub);font-size:13px;padding:4px 0}
.state{font-size:12px;padding:1px 7px;border-radius:10px;background:#f0eefc;color:#4b3fb0}
.state.terminal{background:#f2f2f6;color:var(--sub)}
.state.failed{background:#fdeceb;color:var(--warn)}
.mono{font-family:ui-monospace,Consolas,monospace;font-size:12px;color:var(--sub);word-break:break-all}
.row{display:flex;gap:12px;flex-wrap:wrap;align-items:center}
.grow{flex:1;min-width:220px}
.topbar{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:20px}
.login{max-width:360px;margin:12vh auto 0;background:var(--card);border:1px solid var(--line);
 border-radius:12px;padding:28px}
.login h1{font-size:20px}
.login button{width:100%;height:36px;margin-top:14px;font-size:14px}
.err{color:var(--warn);font-size:12px;margin:10px 0 0}
.ok{color:#2f7a52;font-size:12px}
</style></head><body>

<div id="login" class="login">
  <h1>Agent 控制中心</h1>
  <p class="sub">输入应用内「Agent 控制中心」窗口显示的会话令牌</p>
  <input id="tokenInput" type="password" placeholder="会话令牌" autocomplete="off">
  <button id="loginBtn" class="primary">进入</button>
  <p id="loginError" class="err" hidden></p>
</div>

<div id="app" hidden>
  <div class="topbar">
    <div><h1>Agent 控制中心</h1><p class="sub">数据来自本机真实运行状态</p></div>
    <button id="logoutBtn">退出</button>
  </div>

  <section>
    <h2>设置</h2>
    <p class="sub" style="margin-bottom:4px">留空的字段不会修改。密钥只写不读回，页面永远看不到已保存的密钥。</p>

    <h3>主模型</h3>
    <div class="grid2">
      <div><label>服务商</label><input id="llm-provider" placeholder="Kimi"></div>
      <div><label>Base URL</label><input id="llm-baseUrl" placeholder="https://..."></div>
      <div><label>模型</label><input id="llm-model" placeholder="kimi-k2.6"></div>
      <div><label>API Key</label><input id="llm-apiKey" type="password" autocomplete="off"></div>
    </div>

    <h3>备用模型</h3>
    <div class="grid2">
      <div><label>服务商</label><input id="fallbackLlm-provider"></div>
      <div><label>Base URL</label><input id="fallbackLlm-baseUrl" placeholder="https://..."></div>
      <div><label>模型</label><input id="fallbackLlm-model"></div>
      <div><label>API Key</label><input id="fallbackLlm-apiKey" type="password" autocomplete="off"></div>
    </div>

    <h3>语音识别 / 语音合成 / 视觉</h3>
    <div class="grid2">
      <div><label>识别服务商</label><input id="asr-provider"></div>
      <div><label>识别 Base URL</label><input id="asr-baseUrl" placeholder="https://..."></div>
      <div><label>识别 API Key</label><input id="asr-apiKey" type="password" autocomplete="off"></div>
      <div><label>合成服务商</label><input id="tts-provider"></div>
      <div><label>合成 Base URL</label><input id="tts-baseUrl" placeholder="https://..."></div>
      <div><label>合成模型</label><input id="tts-model"></div>
      <div><label>音色</label><input id="tts-voice"></div>
      <div><label>合成 API Key</label><input id="tts-apiKey" type="password" autocomplete="off"></div>
      <div><label>视觉服务商</label><input id="vision-provider"></div>
      <div><label>视觉 Base URL</label><input id="vision-baseUrl" placeholder="https://..."></div>
      <div><label>视觉模型</label><input id="vision-model"></div>
      <div><label>视觉 API Key</label><input id="vision-apiKey" type="password" autocomplete="off"></div>
    </div>

    <h3>人格与唤醒</h3>
    <div class="grid2">
      <div><label>名字</label><input id="personalityName"></div>
      <div><label>唤醒词</label><input id="wakeWord"></div>
    </div>
    <label>系统提示词</label>
    <textarea id="systemPrompt" rows="4"></textarea>

    <div class="row" style="margin-top:14px">
      <button id="saveSettingsBtn" class="primary">保存设置</button>
      <span id="settingsStatus"></span>
    </div>
  </section>

  <section>
    <h2>消息平台账号</h2>
    <p class="sub">先选平台再添加。可以加多个账号，之后随时在这里管理；未实现的平台不会出现在可选列表里。</p>
    <div class="row">
      <select id="platformSelect" class="grow"></select>
      <input id="accountName" placeholder="账号名称（如 主号 / 测试群）" style="max-width:220px">
      <button id="addAccountBtn" class="primary">添加</button>
    </div>
    <div id="credentialFields" class="grid2" style="margin-top:10px"></div>
    <p id="accountStatus" class="sub"></p>
    <table><thead><tr><th>账号</th><th>平台</th><th>凭据</th><th>操作</th></tr></thead>
    <tbody id="accounts"></tbody></table>
    <div id="accountsEmpty" class="empty" hidden>还没有添加账号。</div>
  </section>

  <section>
    <h2>局域网接收（LocalSend）</h2>
    <p class="sub">默认关闭。开启后会在所有网卡上监听 53317 端口；别人发来的文件必须先经过待审批区，你同意后才会写盘，拒绝会回 403。</p>
    <div class="row">
      <span id="lsState" class="mono">状态未知</span>
      <button id="lsToggleBtn">切换接收</button>
    </div>
    <div class="grid2" style="margin-top:8px">
      <div><label>设备名（其它设备看到的名称）</label><input id="lsAlias"></div>
      <div><label>接收目录</label><input id="lsSaveDir"></div>
    </div>
    <div class="row" style="margin-top:8px">
      <button id="lsSaveBtn" class="primary">保存</button>
      <span id="lsStatus"></span>
    </div>

    <h3>附近设备</h3>
    <table><thead><tr><th>设备</th><th>地址</th><th>最后出现</th><th>操作</th></tr></thead>
    <tbody id="lsPeers"></tbody></table>
    <div id="lsPeersEmpty" class="empty" hidden>还没有发现设备。对方需要运行 LocalSend（或仙灵）并在同一网络内允许被发现。</div>

    <h3>发送文件</h3>
    <label>文件路径（每行一个，必须是本机存在的文件）</label>
    <textarea id="lsPaths" rows="3" placeholder="D:\照片\a.png"></textarea>
    <div class="row" style="margin-top:8px">
      <button id="lsSendBtn" class="primary">发送给选中设备</button>
      <span id="lsSendStatus"></span>
    </div>
  </section>

  <section>
    <h2>配对设备组</h2>
    <p class="sub">配对是后面所有跨设备能力的底座：每个设备组有自己的密钥和自己的数据空间，组与组之间默认互相看不见。端口默认关闭，开了之后同一网络里的设备才能连进来。配对码只显示一次、十分钟内有效、错满次数就作废；配对成功不等于授权，任何能力都要你在这里逐项给。</p>
    <div class="row">
      <span id="prState" class="mono">状态未知</span>
      <button id="prToggleBtn">切换配对端口</button>
      <span id="prDevice" class="mono"></span>
    </div>
    <div class="grid2" style="margin-top:8px">
      <div><label>别的设备上显示的本机名称</label><input id="prDisplayName"></div>
      <div><label>配对端口</label><input id="prPort" type="number" min="1024" max="65535"></div>
    </div>
    <div class="row" style="margin-top:8px">
      <button id="prSaveBtn" class="primary">保存</button>
      <span id="prStatus"></span>
    </div>
    <p id="prNote" class="err" hidden></p>

    <h3>设备组</h3>
    <div class="row">
      <input id="prGroupName" placeholder="新设备组名称，例如 家用" style="max-width:220px">
      <button id="prCreateGroupBtn" class="primary">创建设备组</button>
    </div>
    <div id="prLatestCode" class="ok" hidden></div>
    <table style="margin-top:8px"><thead><tr><th>设备组</th><th>组密钥</th><th>成员</th><th>操作</th></tr></thead>
    <tbody id="prGroups"></tbody></table>
    <div id="prGroupsEmpty" class="empty" hidden>还没有设备组。先创建一个，再生成配对码让别的设备加入。</div>

    <h3>待使用的配对码</h3>
    <table><thead><tr><th>归属设备组</th><th>剩余尝试</th><th>过期时间</th><th>操作</th></tr></thead>
    <tbody id="prInvites"></tbody></table>
    <div id="prInvitesEmpty" class="empty" hidden>当前没有待使用的配对码。</div>
  </section>

  <section>
    <h2>跨设备传输</h2>
    <p class="sub">走已配对设备之间的点对点通道（WebRTC 数据通道），信令经本机配对端口转发，不经过任何公网服务。对方发来的文件会先进入「待审批 - 待回答」，你同意之后才会落盘；落盘前校验整文件哈希，不合格直接丢弃。</p>
    <div class="row">
      <span id="trState" class="mono">状态未知</span>
      <span id="trDest" class="mono"></span>
    </div>
    <div class="grid2" style="margin-top:8px">
      <div><label>接收设备（来自已配对的设备组）</label><select id="trPeer" class="grow"></select></div>
    </div>
    <label>要发送的文件路径（每行一个，必须是本机存在的文件）</label>
    <textarea id="trPaths" rows="3" placeholder="D:\照片\a.png"></textarea>
    <div class="row" style="margin-top:8px">
      <button id="trSendBtn" class="primary">发送</button>
      <span id="trStatus"></span>
    </div>

    <h3>传输记录</h3>
    <table><thead><tr><th>设备</th><th>方向</th><th>状态</th><th>字节</th><th>说明</th></tr></thead>
    <tbody id="trJobs"></tbody></table>
    <div id="trJobsEmpty" class="empty" hidden>还没有传输记录。</div>
  </section>

  <section>
    <h2>通知</h2>
    <p class="sub">这里是仙灵自己的通知历史，不和系统通知混在一起。任务完成、失败、待审批、待回答都会进这里；免打扰时段只静默普通通知，待审批仍然会提醒——把它静音等于让任务停在原地。</p>
    <div class="row">
      <span id="ntState" class="mono">状态未知</span>
      <button id="ntReadAllBtn">全部已读</button>
      <button id="ntClearBtn">清空</button>
    </div>
    <p class="sub mono" id="ntSysState"></p>
    <div class="grid2" style="margin-top:10px">
      <div><label><input type="checkbox" id="ntEnabled"> 启用通知</label></div>
      <div><label><input type="checkbox" id="ntQuiet"> 免打扰</label></div>
      <div><label><input type="checkbox" id="ntCompleted"> 任务完成</label></div>
      <div><label><input type="checkbox" id="ntFailed"> 任务失败</label></div>
      <div><label><input type="checkbox" id="ntApproval"> 待审批</label></div>
      <div><label><input type="checkbox" id="ntQuestion"> 待回答</label></div>
      <div><label><input type="checkbox" id="ntSystem"> 系统通知</label></div>
      <div><label><input type="checkbox" id="ntMirror"> 关键任务也推系统</label></div>
      <div><label><input type="checkbox" id="ntDetails"> 系统通知带具体内容</label></div>
    </div>
    <div class="row" style="margin-top:10px">
      <button id="ntSaveBtn" class="primary">保存通知设置</button>
      <span id="ntStatus"></span>
    </div>
    <table style="margin-top:12px"><thead><tr><th>时间</th><th>类型</th><th>内容</th><th>操作</th></tr></thead>
    <tbody id="ntItems"></tbody></table>
    <div id="ntEmpty" class="empty" hidden>还没有通知。</div>
  </section>

  <section>
    <h2>MCP 后端</h2>
    <p class="sub">可选的外部感知/操作后端（例如 Windows-MCP）。它发布的每个工具都会出现在工具表里，但每次调用仍要你逐次批准，并受上面的暂停/停止与审计约束。未配置或未启用时不会启动任何进程。</p>
    <div class="row">
      <span id="mcpState" class="mono">未连接</span>
      <button id="mcpStartBtn">启动服务器</button>
      <button id="mcpStopBtn">停止并撤下工具</button>
    </div>
    <p class="sub mono" id="mcpDetail"></p>
    <p class="sub mono" id="mcpTools"></p>
  </section>

  <section>
    <h2>屏幕操作</h2>
    <p class="sub">暂停在当前动作做完之后生效，下一个动作不会执行；停止之后一律不执行，继续也不会恢复。每一次点击/输入都要单独审批，未批准的动作不会碰鼠标。</p>
    <div class="row">
      <span id="scState" class="mono">状态未知</span>
      <button id="scPauseBtn">暂停</button>
      <button id="scResumeBtn">继续</button>
      <button id="scStopBtn">停止</button>
    </div>
    <p class="sub mono" id="scDetail"></p>
  </section>

  <section>
    <h2>手机任务摘要</h2>
    <p class="sub">允许已配对的手机读取任务摘要：只有任务名、状态和时间，不含任务内容、输出或文件。默认关闭；手机端也要各自打开。撤销某台设备后，它必须重新配对。</p>
    <div class="row">
      <label><input type="checkbox" id="ssEnabled"> 允许已配对设备读取任务摘要</label>
      <label><input type="checkbox" id="ssPairControl"> 允许已配对手机查看任务并回答待审批</label>
      <label><input type="checkbox" id="ssNotesSync"> 允许已配对手机与这里同步笔记</label>
      <button id="ssSaveBtn" class="primary">保存</button>
      <span id="ssStatus" class="mono"></span>
    </div>
    <table style="margin-top:10px"><thead><tr><th>已配对设备</th><th>操作</th></tr></thead>
    <tbody id="ssDevices"></tbody></table>
    <div id="ssEmpty" class="empty" hidden>还没有持有令牌的设备。手机完成配对后会出现在这里。</div>
  </section>

  <section>
    <h2>Home Assistant</h2>
    <p class="sub">可选客户端，服务端仍是你自己的 Home Assistant，本程序不附带也不嵌入它。读状态不需要确认；控制设备、执行场景、触发自动化都会先进待审批，你点同意才发得出去。</p>
    <label>服务器地址</label>
    <input id="haServer" placeholder="192.168.1.10:8123">
    <div class="grid2" style="margin-top:10px">
      <div><label>OAuth client_id</label><input id="haClient" placeholder="http://127.0.0.1:8137"></div>
      <div><label>回调地址</label><input id="haRedirect" placeholder="http://127.0.0.1:8137/callback"></div>
    </div>
    <div class="row" style="margin-top:10px">
      <button id="haSaveBtn">保存地址</button>
      <button id="haBeginBtn">生成授权链接</button>
      <button id="haConnectBtn">读取状态</button>
      <button id="haSignOutBtn">登出</button>
      <span id="haState" class="mono">未连接</span>
    </div>
    <label>授权链接（在浏览器打开；同意后浏览器会跳到一个打不开的地址，把地址栏里整条粘回下面）</label>
    <input id="haAuthorizeUrl" readonly placeholder="点上面的「生成授权链接」">
    <div class="row" style="margin-top:10px">
      <input id="haCallback" placeholder="http://127.0.0.1:8137/callback?code=...&state=...">
      <button id="haCompleteBtn">完成授权</button>
    </div>
    <div class="row" style="margin-top:10px">
      <input id="haToken" placeholder="或者用长期访问令牌（备用方式，在 HA 个人资料里自己创建）">
      <button id="haTokenBtn">用令牌连接</button>
    </div>
    <div id="haError" class="sub" style="margin-top:8px"></div>
    <p class="sub" style="margin-top:12px"><strong>场景</strong></p>
    <table><tbody id="haScenes"></tbody></table>
    <p class="sub" style="margin-top:10px"><strong>自动化</strong></p>
    <table><tbody id="haAutomations"></tbody></table>
    <p class="sub" style="margin-top:10px"><strong>实体</strong></p>
    <table><thead><tr><th>实体</th><th>状态</th><th>操作</th></tr></thead><tbody id="haEntities"></tbody></table>
    <div id="haEmpty" class="empty" hidden>还没有连接到 Home Assistant。</div>
  </section>

  <section>
    <h2>Fairy Code</h2>
    <p class="sub">Pi Agent 主循环。每一次工具调用都会先经过审批闸门：文件覆盖、命令执行、凭据路径都需要你在这里点同意。</p>
    <div class="row">
      <span id="fcEngine" class="mono">引擎状态未检测</span>
      <button id="fcProbeBtn">检测引擎</button>
    </div>
    <label>工作区（必须是已存在的目录）</label>
    <input id="fcWorkspace" placeholder="D:\Dev\MyProject">
    <label>任务描述</label>
    <textarea id="fcPrompt" rows="3" placeholder="例如：修复 Tests/Foo 里失败的用例，先只读分析再改"></textarea>
    <div class="row" style="margin-top:10px">
      <button id="fcStartBtn" class="primary">启动任务</button>
      <span id="fcStatus"></span>
    </div>
    <table style="margin-top:12px"><thead><tr><th>任务</th><th>工作区</th><th>引擎 / 模型</th><th>操作</th></tr></thead>
    <tbody id="fcSessions"></tbody></table>
    <div id="fcEmpty" class="empty" hidden>还没有启动过 Fairy Code 任务。</div>
  </section>

  <section>
    <h2>任务</h2>
    <table><thead><tr><th>任务</th><th>状态</th><th>进度</th><th>引擎 / 模型</th><th>操作</th></tr></thead>
    <tbody id="tasks"></tbody></table>
    <div id="tasksEmpty" class="empty" hidden>当前没有任务。</div>
  </section>

  <section>
    <h2>待审批 / 待回答</h2>
    <table><thead><tr><th>类型</th><th>内容</th><th>风险</th><th>操作</th></tr></thead>
    <tbody id="pending"></tbody></table>
    <div id="pendingEmpty" class="empty" hidden>没有待处理项。</div>
  </section>

  <section>
    <h2>资源</h2>
    <table><tbody id="resources"></tbody></table>
  </section>

  <section>
    <h2>最近事件</h2>
    <table><thead><tr><th>时间</th><th>类型</th><th>详情</th></tr></thead>
    <tbody id="events"></tbody></table>
  </section>

  <section>
    <h2>操作审计</h2>
    <table><thead><tr><th>时间</th><th>动作</th><th>类别</th><th>决策</th><th>理由</th></tr></thead>
    <tbody id="audit"></tbody></table>
  </section>
</div>

<script>
const $ = id => document.getElementById(id);
let token = sessionStorage.getItem('fairyToken') || '';
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

const endpointFields = {
  llm: ['provider','baseUrl','model','apiKey'],
  fallbackLlm: ['provider','baseUrl','model','apiKey'],
  asr: ['provider','baseUrl','apiKey'],
  tts: ['provider','baseUrl','model','voice','apiKey'],
  vision: ['provider','baseUrl','model','apiKey']
};

function cell(html){const td=document.createElement('td');td.innerHTML=html;return td;}
function row(cells){const tr=document.createElement('tr');cells.forEach(c=>tr.appendChild(c));return tr;}

async function api(path){
  const headers = {};
  if (token) headers['X-Fairy-Token'] = token;
  const r = await fetch(path, { headers, cache:'no-store' });
  return { status: r.status, body: await r.json().catch(() => ({})) };
}

async function post(path, payload){
  const r = await fetch(path, {
    method:'POST',
    headers:{ 'Content-Type':'application/json', 'X-Fairy-Token': token },
    body: JSON.stringify(payload ?? {})
  });
  return { status: r.status, body: await r.json().catch(() => ({})) };
}

function showApp(){
  $('login').hidden = true;
  $('app').hidden = false;
  refresh();
  setInterval(refresh, 2000);
}

function showLogin(message){
  $('app').hidden = true;
  $('login').hidden = false;
  if (message) { $('loginError').textContent = message; $('loginError').hidden = false; }
}

async function login(){
  const candidate = $('tokenInput').value.trim();
  if (!candidate) { showLogin('请输入令牌'); return; }
  const saved = token;
  token = candidate;
  const { status } = await api('/api/session');
  if (status === 200) {
    sessionStorage.setItem('fairyToken', token);
    $('loginError').hidden = true;
    showApp();
  } else {
    token = saved;
    showLogin('令牌无效');
  }
}

function logout(){
  sessionStorage.removeItem('fairyToken');
  token = '';
  $('tokenInput').value = '';
  showLogin('');
}

function fillSettings(s){
  for (const group of Object.keys(endpointFields)) {
    const data = s[group] || {};
    for (const key of endpointFields[group]) {
      const el = $(group + '-' + key);
      if (!el) continue;
      if (key === 'apiKey') {
        el.value = '';
        el.placeholder = data.hasApiKey ? '已设置，留空则不修改' : '未设置';
      } else {
        el.value = data[key] || '';
      }
    }
  }
  $('personalityName').value = s.personalityName || '';
  $('systemPrompt').value = s.systemPrompt || '';
  $('wakeWord').value = s.wakeWord || '';
}

function collectSettings(){
  const out = {};
  for (const group of Object.keys(endpointFields)) {
    const payload = {};
    for (const key of endpointFields[group]) {
      const el = $(group + '-' + key);
      if (!el) continue;
      const value = el.value.trim();
      if (value) payload[key] = value;
    }
    if (Object.keys(payload).length) out[group] = payload;
  }
  const name = $('personalityName').value.trim();
  if (name) out.personalityName = name;
  const prompt = $('systemPrompt').value.trim();
  if (prompt) out.systemPrompt = prompt;
  const wake = $('wakeWord').value.trim();
  if (wake) out.wakeWord = wake;
  return out;
}

async function loadSettings(){
  const { status, body } = await api('/api/settings');
  if (status === 200) fillSettings(body);
}

async function saveSettings(){
  const payload = collectSettings();
  if (!Object.keys(payload).length) { $('settingsStatus').className = 'err'; $('settingsStatus').textContent = '没有需要保存的内容'; return; }
  const { status, body } = await post('/api/settings', payload);
  const el = $('settingsStatus');
  if (status === 200) {
    el.className = 'ok';
    el.textContent = '已保存';
    fillSettings(body);
    for (const key of document.querySelectorAll('input[type=password]')) key.value = '';
  } else {
    el.className = 'err';
    el.textContent = body.message || ('保存失败（' + status + '）');
  }
}

function stateBadge(state){
  const cls = ['Completed','Failed','Cancelled'].includes(state)
    ? (state === 'Failed' ? 'state failed' : 'state terminal') : 'state';
  return `<span class="${cls}">${esc(state)}</span>`;
}

async function loadTasks(){
  const { body } = await api('/api/tasks');
  const tasks = body.tasks || [];
  $('tasks').replaceChildren(...tasks.map(t => row([
    cell(`<div>${esc(t.title)}</div><div class="mono">${esc(t.id)}</div>`),
    cell(stateBadge(t.state) + (t.pauseRequested ? ' <span class="mono">暂停已请求</span>' : '')),
    cell(t.totalSteps ? `${t.step}/${t.totalSteps}<div class="mono">${esc(t.stepLabel || '')}</div>` : '—'),
    cell(`${esc(t.engine || '—')}${t.model ? '<div class="mono">' + esc(t.model) + '</div>' : ''}`),
    cell(`<button data-act="pause" data-id="${esc(t.id)}" ${t.canPause ? '' : 'disabled'}>暂停</button>
          <button data-act="resume" data-id="${esc(t.id)}" ${t.canResume ? '' : 'disabled'}>继续</button>
          <button data-act="stop" data-id="${esc(t.id)}" class="danger" ${t.canStop ? '' : 'disabled'}>停止</button>`)
  ])));
  $('tasksEmpty').hidden = tasks.length > 0;
}

async function loadPending(){
  const { body } = await api('/api/pending');
  const items = body.pending || [];
  $('pending').replaceChildren(...items.map(p => {
    const actions = p.kind === 'Approval'
      ? `<button class="primary" data-answer="${esc(p.id)}" data-approved="1">同意</button>
         <button class="danger" data-answer="${esc(p.id)}" data-approved="0">拒绝</button>`
      : `<input id="ans-${esc(p.id)}" placeholder="回答" style="width:180px;display:inline-block">
         <button class="primary" data-text="${esc(p.id)}">提交</button>`;
    return row([
      cell(p.kind === 'Approval' ? '审批' : '提问'),
      cell(`<div>${esc(p.prompt).replace(/\n/g,'<br>')}</div>`),
      cell(esc(p.riskText || '—')),
      cell(actions)
    ]);
  }));
  $('pendingEmpty').hidden = items.length > 0;
}

async function loadResources(){
  const { body } = await api('/api/resources');
  const mb = b => (b / 1048576).toFixed(1) + ' MB';
  const h = Math.floor((body.uptimeSeconds||0)/3600), m = Math.floor(((body.uptimeSeconds||0)%3600)/60);
  $('resources').replaceChildren(
    row([cell('工作集'), cell(mb(body.workingSetBytes||0))]),
    row([cell('私有内存'), cell(mb(body.privateBytes||0))]),
    row([cell('线程数'), cell(String(body.threadCount ?? 0))]),
    row([cell('运行时长'), cell(`${h} 小时 ${m} 分`)])
  );
}

async function loadEvents(){
  const { body } = await api('/api/events');
  const events = (body.events || []).slice().reverse().slice(0, 20);
  $('events').replaceChildren(...events.map(e => row([
    cell(`<span class="mono">${esc(new Date(e.at).toLocaleTimeString())}</span>`),
    cell(esc(e.type)),
    cell(esc(e.detail))
  ])));
}

async function loadAudit(){
  const { status, body } = await api('/api/audit');
  if (status !== 200) { $('audit').replaceChildren(); return; }
  const records = (body.records || []).slice().reverse();
  $('audit').replaceChildren(...records.map(r => row([
    cell(`<span class="mono">${esc(new Date(r.at).toLocaleTimeString())}</span>`),
    cell(`${esc(r.actionId)}<div class="mono">${esc(r.summary || '')}</div>`),
    cell(esc(r.categoryText)),
    cell(esc(r.decision)),
    cell(esc(r.reason))
  ])));
}

async function loadFairyCode(){
  const { status, body } = await api('/api/fairycode');
  const host = $('fcSessions');
  if (status !== 200) {
    host.replaceChildren();
    $('fcEmpty').hidden = false;
    $('fcEmpty').textContent = body.message || 'Fairy Code 未启用';
    return;
  }
  const sessions = body.sessions || [];
  host.replaceChildren(...sessions.slice().reverse().map(s => row([
    cell(`<div>${esc(s.title)}</div><div class="mono">${esc(s.taskId)}</div>` +
         (s.lastError ? `<div class="err">${esc(s.lastError)}</div>` : '')),
    cell(`<span class="mono">${esc(s.workspace)}</span>`),
    cell(`${esc(s.engine || '')}<div class="mono">${esc(s.model || '')}</div>`),
    cell(s.running
      ? `<button class="danger" data-fcstop="${esc(s.taskId)}">停止</button>`
      : '<span class="mono">已结束</span>')
  ])));
  $('fcEmpty').hidden = sessions.length > 0;
}

async function probeFairyCode(){
  const el = $('fcEngine');
  el.textContent = '检测中…';
  const { body } = await post('/api/fairycode/probe');
  el.textContent = body.ready
    ? `引擎可用：Pi Agent ${esc(body.version || '')}`
    : `引擎不可用：${body.detail || body.message || '未知原因'}`;
}

async function startFairyCode(){
  const prompt = $('fcPrompt').value.trim();
  const workspace = $('fcWorkspace').value.trim();
  const el = $('fcStatus');
  if (!prompt) { el.className = 'err'; el.textContent = '请先写任务描述'; return; }
  const { status, body } = await post('/api/fairycode/start', { prompt, workspace });
  if (status === 200) {
    el.className = 'ok';
    el.textContent = '任务已启动，工具调用会出现在待审批区';
    $('fcPrompt').value = '';
    loadFairyCode(); refresh();
  } else {
    el.className = 'err';
    el.textContent = body.message || ('启动失败（' + status + '）');
  }
}

async function stopFairyCode(id){
  await post(`/api/fairycode/sessions/${id}/stop`);
  loadFairyCode(); refresh();
}

async function loadLocalSend(){
  const { status, body } = await api('/api/localsend');
  const state = $('lsState');
  if (status !== 200) { state.textContent = body.message || '局域网接收不可用'; return; }
  state.textContent = (body.enabled ? '已开启' : '已关闭') +
    (body.running ? ` · 正在监听 ${body.port}` : ' · 未监听') +
    (body.discoveryRunning ? ' · 发现中' : '');
  $('lsAlias').value = body.alias || '';
  $('lsSaveDir').value = body.saveDirectory || '';
  state.dataset.enabled = body.enabled ? '1' : '0';

  const peers = body.peers || [];
  if (peers.length && !peers.some(p => p.fingerprint === selectedPeer)) selectedPeer = peers[0].fingerprint;
  if (!peers.length) selectedPeer = '';
  $('lsPeers').replaceChildren(...peers.map(p => row([
    cell(`${esc(p.alias)}<div class="mono">${esc(p.deviceType)}</div>`),
    cell(`<span class="mono">${esc(p.endpoint || '—')}</span>`),
    cell(`<span class="mono">${esc(new Date(p.lastSeen).toLocaleTimeString())}</span>`),
    cell(`<button data-lspeer="${esc(p.fingerprint)}">${p.fingerprint === selectedPeer ? '已选中' : '选择'}</button>`)
  ])));
  $('lsPeersEmpty').hidden = peers.length > 0;
}

let selectedPeer = '';

async function sendLocalSend(){
  const el = $('lsSendStatus');
  if (!selectedPeer) { el.className = 'err'; el.textContent = '先选择一台设备'; return; }
  const paths = $('lsPaths').value.split('\n').map(line => line.trim()).filter(Boolean);
  if (!paths.length) { el.className = 'err'; el.textContent = '请填写要发送的文件路径'; return; }

  el.className = 'sub';
  el.textContent = '发送中…';
  const { status, body } = await post('/api/localsend/send', { fingerprint: selectedPeer, paths });
  el.className = status === 200 ? 'ok' : 'err';
  el.textContent = body.message || (status === 200 ? '已发送' : ('失败（' + status + '）'));
  if (status === 200) $('lsPaths').value = '';
  loadLocalSend();
}

async function toggleLocalSend(){
  const enabled = $('lsState').dataset.enabled !== '1';
  await applyLocalSend({ enabled });
}

async function saveLocalSend(){
  await applyLocalSend({ alias: $('lsAlias').value.trim(), saveDirectory: $('lsSaveDir').value.trim() });
}

async function applyLocalSend(payload){
  const el = $('lsStatus');
  const { status, body } = await post('/api/localsend', payload);
  el.className = status === 200 ? 'ok' : 'err';
  el.textContent = body.message || (status === 200 ? '已保存' : ('失败（' + status + '）'));
  loadLocalSend();
}

let platformMeta = {};
function renderCredentialFields(){
  const host = $('credentialFields');
  host.replaceChildren();
  const meta = platformMeta[$('platformSelect').value];
  if (!meta) return;

  (meta.fields || []).forEach(field => {
    const wrap = document.createElement('div');
    const label = document.createElement('label');
    label.textContent = field.label;
    const input = document.createElement('input');
    input.id = 'cred-' + field.key;
    input.autocomplete = 'off';
    if (field.secret) input.type = 'password';
    input.placeholder = field.label;
    wrap.appendChild(label);
    wrap.appendChild(input);
    host.appendChild(wrap);
  });

  if (!(meta.fields || []).length) {
    const note = document.createElement('p');
    note.className = 'sub';
    note.textContent = '该平台无需在此填写凭据：认证在它自己的终端/控制台完成。';
    host.appendChild(note);
  }

  const hint = document.createElement('p');
  hint.className = 'sub';
  hint.style.display = 'none';
  host.appendChild(hint);
}

async function loadPlatforms(){
  const { body } = await api('/api/platforms');
  const select = $('platformSelect');
  const platforms = (body.platforms || []).filter(p => p.implemented);
  platformMeta = {};
  platforms.forEach(p => { platformMeta[p.kind] = p; });

  select.replaceChildren(...platforms.map(p => {
    const option = document.createElement('option');
    option.value = p.kind;
    option.textContent = p.name;
    return option;
  }));

  if (!platforms.length) {
    const option = document.createElement('option');
    option.textContent = '暂无可接入的平台';
    option.disabled = true;
    select.appendChild(option);
  }

  renderCredentialFields();
}

async function loadAccounts(){
  const { status, body } = await api('/api/accounts');
  if (status !== 200) { $('accountsEmpty').hidden = false; $('accounts').replaceChildren(); return; }
  const accounts = body.accounts || [];
  $('accounts').replaceChildren(...accounts.map(a => row([
    cell(`<div>${esc(a.displayName)}</div><div class="mono">${esc(a.id)}</div>`),
    cell(esc(a.platformName)),
    cell((a.missingFields || []).length
      ? '缺少：' + esc((a.missingFields || []).join('、'))
      : '已齐全'),
    cell(`<button data-probe="${esc(a.id)}">检测</button><button class="danger" data-remove="${esc(a.id)}">删除</button>`)
  ])));
  $('accountsEmpty').hidden = accounts.length > 0;
}

async function addAccount(){
  const platform = $('platformSelect').value;
  const displayName = $('accountName').value.trim();
  const credentials = {};
  const meta = platformMeta[platform] || { fields: [] };
  (meta.fields || []).forEach(field => {
    const input = $('cred-' + field.key);
    if (input && input.value.trim()) credentials[field.key] = input.value.trim();
  });

  const { status, body } = await post('/api/accounts', { platform, displayName, credentials });
  const el = $('accountStatus');
  if (status === 200) {
    el.className = 'ok';
    el.textContent = '已添加';
    $('accountName').value = '';
    (meta.fields || []).forEach(field => { const input = $('cred-' + field.key); if (input) input.value = ''; });
    loadAccounts();
  } else {
    el.className = 'err';
    el.textContent = body.message || ('添加失败（' + status + '）');
  }
}

async function probeAccount(id){
  const { status, body } = await post(`/api/accounts/${id}/probe`);
  const el = $('accountStatus');
  el.className = body.ok ? 'ok' : 'err';
  el.textContent = `${body.state || ''}：${body.message || ('HTTP ' + status)}`;
}

async function removeAccount(id){
  await post(`/api/accounts/${id}/remove`);
  loadAccounts();
}

let pairingModel = { capabilities: [], groups: [] };

async function loadPairing(){
  const { status, body } = await api('/api/pairing');
  const state = $('prState');
  if (status !== 200) { state.textContent = body.message || '配对不可用'; return; }

  pairingModel = { capabilities: body.capabilities || [], groups: body.groups || [] };
  state.textContent = (body.enabled ? '已开启' : '已关闭') + (body.listening ? ` · 正在监听 ${body.port}` : ' · 未监听');
  state.dataset.enabled = body.enabled ? '1' : '0';
  $('prDisplayName').value = body.displayName || '';
  $('prPort').value = body.port || '';
  $('prDevice').textContent = '本机 ' + (body.shortDeviceId || '未知');

  const note = body.listenError || body.loadError;
  $('prNote').hidden = !note;
  $('prNote').textContent = note || '';

  const groups = body.groups || [];
  const rows = [];
  let index = 0;

  for (const g of groups) {
    const groupHead = `${esc(g.name)}${g.active ? ' · 当前组' : ''}<div class="mono">${esc(g.shortId)}</div>`;
    const keyText = `第 ${g.keyEpoch} 代<div class="mono">${esc(g.keyFingerprint)}</div>`;
    const groupOps = `<button data-practive="${esc(g.groupId)}">设为当前</button> ` +
      `<button data-prrotate="${esc(g.groupId)}">换组密钥</button> ` +
      `<button data-prinvite="${esc(g.groupId)}">生成配对码</button>`;

    if (!g.members.length) {
      rows.push(row([cell(groupHead), cell(keyText), cell('<span class="sub">暂无成员</span>'), cell(groupOps)]));
      continue;
    }

    g.members.forEach((m, position) => {
      const granted = m.capabilities || [];
      const options = pairingModel.capabilities
        .map(c => `<option value="${c.name}">${esc(c.text)}${c.requiresApproval ? ' · 每次需确认' : ''}</option>`)
        .join('');
      const selectId = 'prcap' + index++;
      const memberOps = `<select id="${selectId}" style="max-width:180px">${options}</select> ` +
        `<button data-prgrant="${esc(g.groupId)}|${esc(m.deviceId)}" data-prselect="${selectId}">授予</button> ` +
        `<button data-prwithdraw="${esc(g.groupId)}|${esc(m.deviceId)}" data-prselect="${selectId}">收回</button> ` +
        `<button data-prremove="${esc(g.groupId)}|${esc(m.deviceId)}">移出</button>`;

      rows.push(row([
        cell(position === 0 ? groupHead : ''),
        cell(position === 0 ? keyText : ''),
        cell(`${esc(m.displayName)}<div class="mono">${esc(m.shortId)}</div>` +
          `<div class="sub">${esc(granted.length ? granted.join('、') : '未授予任何能力')}</div>`),
        cell((position === 0 ? groupOps + '<div style="margin-top:6px"></div>' : '') + memberOps)
      ]));
    });
  }

  $('prGroups').replaceChildren(...rows);
  $('prGroupsEmpty').hidden = groups.length > 0;

  const invites = body.invites || [];
  $('prInvites').replaceChildren(...invites.map(invite => {
    const group = groups.find(g => g.groupId === invite.groupId);
    return row([
      cell(`${esc(group ? group.name : invite.groupId)}<div class="mono">${esc(invite.groupId)}</div>`),
      cell(`${invite.remainingAttempts} 次`),
      cell(`<span class="mono">${esc(new Date(invite.expiresAt).toLocaleTimeString())}</span>`),
      cell(`<button data-prcancel="${esc(invite.inviteId)}">作废</button>`)
    ]);
  }));
  $('prInvitesEmpty').hidden = invites.length > 0;
}

function showPairingResult(status, body){
  const el = $('prStatus');
  el.className = status === 200 ? 'ok' : 'err';
  el.textContent = body.message || (status === 200 ? '已完成' : ('失败（' + status + '）'));
}

async function applyPairingSettings(payload){
  const { status, body } = await post('/api/pairing/settings', payload);
  showPairingResult(status, body);
  loadPairing();
}

async function togglePairing(){
  await applyPairingSettings({ enabled: $('prState').dataset.enabled !== '1' });
}

async function savePairing(){
  const port = parseInt($('prPort').value, 10);
  await applyPairingSettings({
    displayName: $('prDisplayName').value.trim(),
    port: Number.isNaN(port) ? null : port
  });
}

async function createPairingGroup(){
  const name = $('prGroupName').value.trim();
  if (!name) { showPairingResult(400, { message: '先填一个设备组名称' }); return; }
  const { status, body } = await post('/api/pairing/groups', { name });
  if (status === 200) $('prGroupName').value = '';
  showPairingResult(status, body);
  loadPairing();
}

async function pairingAction(path, payload){
  const { status, body } = await post(path, payload);
  showPairingResult(status, body);
  loadPairing();
}

async function createPairingInvite(groupId){
  const { status, body } = await post('/api/pairing/invites', { groupId });
  showPairingResult(status, body);

  // The code exists only in this response; nothing stores it, so it is shown here and nowhere else.
  const latest = $('prLatestCode');
  if (status === 200 && body.code) {
    latest.hidden = false;
    latest.textContent = `配对码 ${body.code}（十分钟内有效，只显示这一次——填到另一台设备上）`;
  }
  loadPairing();
}

async function loadTransfer(){
  const { status, body } = await api('/api/transfer');
  const state = $('trState');
  if (status !== 200) { state.textContent = body.message || '传输不可用'; return; }

  state.textContent = body.enabled ? '已启用' : '未启用（需先开启配对）';
  $('trDest').textContent = body.destinationRoot ? '接收目录 ' + body.destinationRoot : '';

  const peers = body.peers || [];
  const select = $('trPeer');
  const current = select.value;
  select.replaceChildren(...peers.map(peer => {
    const option = document.createElement('option');
    option.value = peer.deviceId;
    option.textContent = `${peer.displayName}（${peer.groupName}）`;
    return option;
  }));
  if (current && peers.some(peer => peer.deviceId === current)) select.value = current;

  const jobs = body.jobs || [];
  $('trJobs').replaceChildren(...jobs.map(job => row([
    cell(`<span class="mono">${esc(job.peer)}</span>`),
    cell(esc(job.direction)),
    cell(esc(job.state)),
    cell(String(job.bytes || 0)),
    cell(`<span class="sub">${esc(job.error || '')}</span>`)
  ])));
  $('trJobsEmpty').hidden = jobs.length > 0;
}

async function sendTransfer(){
  const el = $('trStatus');
  const deviceId = $('trPeer').value;
  const paths = $('trPaths').value.split('\n').map(line => line.trim()).filter(Boolean);

  if (!deviceId) { el.className = 'err'; el.textContent = '先选择接收设备'; return; }
  if (!paths.length) { el.className = 'err'; el.textContent = '请填写要发送的文件路径'; return; }

  el.className = 'sub';
  el.textContent = '发送中…';
  const { status, body } = await post('/api/transfer/send', { deviceId, paths });
  el.className = status === 200 ? 'ok' : 'err';
  el.textContent = body.message || (status === 200 ? '已发送' : ('失败（' + status + '）'));
  loadTransfer();
}

async function loadNotifications(){
  const { status, body } = await api('/api/notifications');
  const state = $('ntState');
  if (status !== 200) { state.textContent = body.message || '通知不可用'; return; }

  state.textContent = (body.enabled ? '已开启' : '已关闭') + ' · 未读 ' + body.unread +
    (body.quietHoursEnabled ? ` · 免打扰 ${body.quietHoursStart}:00-${body.quietHoursEnd}:00` : '');
  $('ntEnabled').checked = !!body.enabled;
  $('ntQuiet').checked = !!body.quietHoursEnabled;
  $('ntCompleted').checked = !!(body.kinds && body.kinds.taskCompleted);
  $('ntFailed').checked = !!(body.kinds && body.kinds.taskFailed);
  $('ntApproval').checked = !!(body.kinds && body.kinds.approvalPending);
  $('ntQuestion').checked = !!(body.kinds && body.kinds.questionPending);
  $('ntSystem').checked = !!body.systemEnabled;
  $('ntMirror').checked = !!body.mirrorKeyEvents;
  $('ntDetails').checked = !!body.systemDetails;

  const channel = body.systemChannel;
  $('ntSysState').textContent = channel
    ? '系统通知通道：' + channel.platform + (channel.available ? '' : '（不可用：' + (channel.error || '未知') + '）') +
      ' · 已投递 ' + channel.delivered + '，已拦下 ' + channel.suppressed +
      (channel.lastSkipReason ? ' · 最近一次：' + channel.lastSkipReason : '')
    : '系统通知通道：未接入';

  const items = body.items || [];
  $('ntItems').replaceChildren(...items.map(item => row([
    cell(`<span class="mono">${esc(new Date(item.createdAt).toLocaleTimeString())}</span>`),
    cell(esc(item.kind) + (item.unread ? ' <span class="pill">未读</span>' : '')),
    cell(`<strong>${esc(item.title)}</strong><div class="sub">${esc(item.body)}</div>`),
    cell(item.unread ? `<button data-ntread="${esc(item.id)}">标记已读</button>` : '')
  ])));
  $('ntEmpty').hidden = items.length > 0;
}

async function saveNotifications(){
  const el = $('ntStatus');
  const payload = {
    enabled: $('ntEnabled').checked,
    quietHoursEnabled: $('ntQuiet').checked,
    taskCompleted: $('ntCompleted').checked,
    taskFailed: $('ntFailed').checked,
    approvalPending: $('ntApproval').checked,
    questionPending: $('ntQuestion').checked,
    systemEnabled: $('ntSystem').checked,
    mirrorKeyEvents: $('ntMirror').checked,
    systemDetails: $('ntDetails').checked
  };

  const { status, body } = await post('/api/notifications/settings', payload);
  el.className = status === 200 ? 'ok' : 'err';
  el.textContent = body.message || (status === 200 ? '已保存' : ('失败（' + status + '）'));
  loadNotifications();
}

async function notificationAction(path){
  const { status, body } = await post(path);
  showResult($('ntStatus'), status, body);
  loadNotifications();
}

const HA_CONTROLLABLE = { light:'', switch:'', fan:'', input_boolean:'', cover:'', media_player:'', climate:'' };

async function loadHomeAssistant(){
  const { status, body } = await api('/api/homeassistant');
  const state = $('haState');
  if (status !== 200) { state.textContent = body.message || 'Home Assistant 不可用'; return; }

  if (body.serverUrl) $('haServer').value = body.serverUrl;
  if (body.redirectUri) $('haRedirect').value = body.redirectUri;

  state.textContent = (body.connected ? '已连接 ' + (body.locationName || '') : '未连接') +
    (body.version ? ' · ' + body.version : '') +
    (body.pendingAuthorizeUrl ? ' · 等待授权' : '');
  $('haAuthorizeUrl').value = body.pendingAuthorizeUrl || '';
  $('haError').textContent = body.error || (body.entityTotal ? '共 ' + body.entityTotal + ' 个实体' : '');
  $('haError').className = body.error ? 'sub err' : 'sub';

  $('haScenes').replaceChildren(...(body.scenes || []).map(scene => row([
    cell(esc(scene.name)),
    cell(`<button data-hacall="scene.turn_on|${esc(scene.entityId)}">执行</button>`)
  ])));

  $('haAutomations').replaceChildren(...(body.automations || []).map(item => row([
    cell(esc(item.name) + ' <span class="mono">' + esc(item.state) + '</span>'),
    cell(`<button data-hacall="automation.trigger|${esc(item.entityId)}">触发</button>`)
  ])));

  const entities = body.entities || [];
  const shown = entities.slice(0, 80);
  const entityRows = shown.map(entity => row([
    cell(`<strong>${esc(entity.friendlyName)}</strong><div class="mono">${esc(entity.entityId)}</div>`),
    cell(esc(entity.state) + (entity.unit ? ' ' + esc(entity.unit) : '')),
    cell(Object.prototype.hasOwnProperty.call(HA_CONTROLLABLE, entity.domain)
      ? `<button data-hacall="${esc(entity.domain)}.turn_on|${esc(entity.entityId)}">开</button>
         <button data-hacall="${esc(entity.domain)}.turn_off|${esc(entity.entityId)}">关</button>`
      : '')
  ]));
  if (entities.length > shown.length)
    entityRows.push(row([cell(`只显示前 ${shown.length} 个，共 ${body.entityTotal} 个。`) ]));
  $('haEntities').replaceChildren(...entityRows);

  $('haEmpty').hidden = body.connected;
}

async function haSaveSettings(){
  const { status, body } = await post('/api/homeassistant/settings', {
    serverUrl: $('haServer').value.trim(),
    clientId: $('haClient').value.trim(),
    redirectUri: $('haRedirect').value.trim()
  });
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function haBegin(){
  const { status, body } = await post('/api/homeassistant/authorize', {});
  showResult($('haError'), status, body);
  if (body.authorizeUrl) $('haAuthorizeUrl').value = body.authorizeUrl;
  loadHomeAssistant();
}

async function haComplete(){
  const { status, body } = await post('/api/homeassistant/complete', { callback: $('haCallback').value.trim() });
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function haUseToken(){
  const { status, body } = await post('/api/homeassistant/token', { token: $('haToken').value.trim() });
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function haConnect(){
  const { status, body } = await post('/api/homeassistant/connect', {});
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function haSignOut(){
  const { status, body } = await post('/api/homeassistant/signout', {});
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function haCall(spec){
  const [service, entityId] = spec.split('|');
  const dot = service.indexOf('.');
  const { status, body } = await post('/api/homeassistant/call', {
    domain: service.slice(0, dot),
    service: service.slice(dot + 1),
    entityId: entityId || null
  });
  showResult($('haError'), status, body);
  loadHomeAssistant();
}

async function loadSummarySync(){
  const { status, body } = await api('/api/summary-sync');
  const state = $('ssStatus');
  if (status !== 200) { state.textContent = body.message || '摘要同步不可用'; return; }

  $('ssEnabled').checked = !!body.enabled;
  $('ssPairControl').checked = !!body.pairControlEnabled;
  $('ssNotesSync').checked = !!body.notesSyncEnabled;
  state.textContent = (body.enabled ? '已允许' : '未允许') + ' · ' + body.pairedDeviceCount + ' 台设备' +
    (body.lastError ? ' · ' + body.lastError : '');

  const devices = body.devices || [];
  $('ssDevices').replaceChildren(...devices.map(device => row([
    cell(esc(device.deviceName)),
    cell(`<button data-ssrevoke="${esc(device.deviceName)}">撤销令牌</button>`)
  ])));
  $('ssEmpty').hidden = devices.length > 0;
}

async function saveSummarySync(){
  const summary = await post('/api/summary-sync/settings', { enabled: $('ssEnabled').checked });
  const control = await post('/api/summary-sync/pair-control', { enabled: $('ssPairControl').checked });
  const notes = await post('/api/summary-sync/notes', { enabled: $('ssNotesSync').checked });
  const ok = summary.status === 200 && control.status === 200 && notes.status === 200;
  showResult($('ssStatus'), ok ? 200 : 409,
    { message: (summary.body.message || '') + '；' + (control.body.message || '') + '；' + (notes.body.message || '') });
  loadSummarySync();
}

async function loadScreenSession(){
  const { status, body } = await api('/api/screen');
  const state = $('scState');
  if (status !== 200) { state.textContent = body.message || '屏幕操作不可用'; return; }

  const labels = { Running: '运行中', Paused: '已暂停', Stopped: '已停止' };
  state.textContent = (labels[body.state] || body.state) + (body.pausePending ? '（暂停将在当前动作结束后生效）' : '') +
    (body.supported ? '' : ' · 本平台不支持');

  $('scDetail').textContent = [
    '平台 ' + body.platform,
    '在飞行 ' + body.inFlight,
    '已完成 ' + body.completed,
    '被拒 ' + body.refused,
    body.lastRefusal ? '最近拒绝：' + body.lastRefusal : '',
    body.unsupportedReason ? '原因：' + body.unsupportedReason : ''
  ].filter(Boolean).join(' · ');
}

async function screenAction(path){
  const { status, body } = await post(path);
  showResult($('scDetail'), status, body);
  loadScreenSession();
}

async function loadMcp(){
  const { status, body } = await api('/api/mcp');
  const state = $('mcpState');
  if (status !== 200) { state.textContent = body.message || 'MCP 后端不可用'; return; }

  state.textContent = (body.connected ? '已连接 ' + (body.serverName || 'MCP 服务器') : (body.enabled ? '已启用，未连接' : '未启用')) +
    (body.connected ? ' · ' + body.toolCount + ' 个工具' : '');
  $('mcpDetail').textContent = [
    body.serverCommand ? '命令 ' + body.serverCommand : '还没有配置服务器命令',
    body.serverVersion ? '版本 ' + body.serverVersion : '',
    body.error ? '错误：' + body.error : ''
  ].filter(Boolean).join(' · ');
  $('mcpTools').textContent = (body.toolNames && body.toolNames.length) ? '工具：' + body.toolNames.join('、') : '';
}

async function mcpAction(path){
  const { status, body } = await post(path);
  showResult($('mcpDetail'), status, body);
  loadMcp();
}

function refresh(){
  loadTasks(); loadPending(); loadResources(); loadEvents(); loadAudit(); loadAccounts(); loadFairyCode(); loadLocalSend(); loadPairing(); loadTransfer(); loadNotifications(); loadHomeAssistant(); loadSummarySync(); loadScreenSession(); loadMcp();
}

$('loginBtn').addEventListener('click', login);
$('tokenInput').addEventListener('keydown', ev => { if (ev.key === 'Enter') login(); });
$('logoutBtn').addEventListener('click', logout);
$('saveSettingsBtn').addEventListener('click', saveSettings);
$('addAccountBtn').addEventListener('click', addAccount);
$('platformSelect').addEventListener('change', renderCredentialFields);
$('fcProbeBtn').addEventListener('click', probeFairyCode);
$('fcStartBtn').addEventListener('click', startFairyCode);
$('lsToggleBtn').addEventListener('click', toggleLocalSend);
$('lsSaveBtn').addEventListener('click', saveLocalSend);
$('lsSendBtn').addEventListener('click', sendLocalSend);
$('prToggleBtn').addEventListener('click', togglePairing);
$('prSaveBtn').addEventListener('click', savePairing);
$('prCreateGroupBtn').addEventListener('click', createPairingGroup);
$('trSendBtn').addEventListener('click', sendTransfer);
$('ntSaveBtn').addEventListener('click', saveNotifications);
$('haSaveBtn').addEventListener('click', haSaveSettings);
$('haBeginBtn').addEventListener('click', haBegin);
$('haCompleteBtn').addEventListener('click', haComplete);
$('haTokenBtn').addEventListener('click', haUseToken);
$('haConnectBtn').addEventListener('click', haConnect);
$('haSignOutBtn').addEventListener('click', haSignOut);
$('ssSaveBtn').addEventListener('click', saveSummarySync);
$('scPauseBtn').addEventListener('click', () => screenAction('/api/screen/pause'));
$('scResumeBtn').addEventListener('click', () => screenAction('/api/screen/resume'));
$('scStopBtn').addEventListener('click', () => screenAction('/api/screen/stop'));
$('mcpStartBtn').addEventListener('click', () => mcpAction('/api/mcp/start'));
$('mcpStopBtn').addEventListener('click', () => mcpAction('/api/mcp/stop'));

document.addEventListener('click', ev => {
  const purge = ev.target.closest('button[data-ssrevoke]');
  if (!purge) return;
  post('/api/summary-sync/devices/' + encodeURIComponent(purge.dataset.ssrevoke) + '/revoke')
    .then(({ status, body }) => { showResult($('ssStatus'), status, body); loadSummarySync(); });
});

document.addEventListener('click', ev => {
  const b = ev.target.closest('button[data-hacall]');
  if (!b) return;
  haCall(b.dataset.hacall);
});

$('ntReadAllBtn').addEventListener('click', () => notificationAction('/api/notifications/read-all'));
$('ntClearBtn').addEventListener('click', () => notificationAction('/api/notifications/clear'));

document.addEventListener('click', ev => {
  const b = ev.target.closest('button');
  if (!b) return;
  if (b.dataset.act) post(`/api/tasks/${b.dataset.id}/${b.dataset.act}`).then(refresh);
  else if (b.dataset.answer) post(`/api/interactions/${b.dataset.answer}/answer`, { approved: b.dataset.approved === '1' }).then(refresh);
  else if (b.dataset.probe) probeAccount(b.dataset.probe);
  else if (b.dataset.remove) removeAccount(b.dataset.remove);
  else if (b.dataset.fcstop) stopFairyCode(b.dataset.fcstop);
  else if (b.dataset.lspeer) { selectedPeer = b.dataset.lspeer; loadLocalSend(); }
  else if (b.dataset.prgrant || b.dataset.prwithdraw) {
    const target = (b.dataset.prgrant || b.dataset.prwithdraw).split('|');
    const select = b.dataset.prselect ? $(b.dataset.prselect) : null;
    const verb = b.dataset.prgrant ? 'grant' : 'withdraw';
    pairingAction(`/api/pairing/groups/${target[0]}/members/${target[1]}/${verb}`,
      { capability: select ? select.value : '' });
  }
  else if (b.dataset.prremove) {
    const target = b.dataset.prremove.split('|');
    pairingAction(`/api/pairing/groups/${target[0]}/members/${target[1]}/remove`);
  }
  else if (b.dataset.practive) pairingAction(`/api/pairing/groups/${b.dataset.practive}/active`);
  else if (b.dataset.prrotate) pairingAction(`/api/pairing/groups/${b.dataset.prrotate}/rotate`);
  else if (b.dataset.prinvite) createPairingInvite(b.dataset.prinvite);
  else if (b.dataset.prcancel) pairingAction(`/api/pairing/invites/${b.dataset.prcancel}/cancel`);
  else if (b.dataset.ntread) post('/api/notifications/read', { id: b.dataset.ntread }).then(loadNotifications);
  else if (b.dataset.text) {
    const input = $('ans-' + b.dataset.text);
    post(`/api/interactions/${b.dataset.text}/answer`, { text: input ? input.value : '' }).then(refresh);
  }
});

(async () => {
  if (token) {
    const { status } = await api('/api/session');
    if (status === 200) { showApp(); loadSettings(); loadPlatforms(); return; }
    sessionStorage.removeItem('fairyToken');
    token = '';
  }
  showLogin('');
})();
</script></body></html>
""";
}