namespace DashboardTests;

/// <summary>A Home Assistant control that answers without a network, for the API routing tests.</summary>
internal sealed class FakeHomeAssistantControl : MyAiAssistant.Services.IHomeAssistantControl
{
    public string? LastSettings { get; private set; }

    public string? LastCall { get; private set; }

    public string? LastToken { get; private set; }

    public bool SignedOut { get; private set; }

    public MyAiAssistant.Services.HomeAssistantView Read() => new(
        Configured: true,
        Connected: true,
        ServerUrl: "http://192.168.1.10:8123/",
        LocationName: "测试小屋",
        Version: "2024.1",
        Error: null,
        PendingAuthorizeUrl: null,
        RedirectUri: "http://127.0.0.1:8137/callback",
        LastRefreshed: DateTimeOffset.UtcNow,
        EntityTotal: 1,
        Entities: [new MyAiAssistant.Services.HaEntityView("light.kitchen", "light", "厨房灯", "on", null)],
        Scenes: [new MyAiAssistant.Services.HaSceneView("scene.movie", "观影模式")],
        Automations: [new MyAiAssistant.Services.HaAutomationView("automation.wake", "早上起床", "on")]);

    public MyAiAssistant.Services.HomeAssistantActionResult ApplySettings(string serverUrl, string? clientId, string? redirectUri)
    {
        // Deliberately shallow: the real URL validation is covered by Tests/HomeAssistant, and this
        // file only exists to prove the routing.
        if (string.IsNullOrWhiteSpace(serverUrl))
            return new MyAiAssistant.Services.HomeAssistantActionResult(false, "请填写地址");
        if (serverUrl.Contains("://", StringComparison.Ordinal) &&
            !serverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new MyAiAssistant.Services.HomeAssistantActionResult(false, "只支持 http 或 https 地址");

        LastSettings = serverUrl;
        return new MyAiAssistant.Services.HomeAssistantActionResult(true, "已保存服务器地址");
    }

    public MyAiAssistant.Services.HomeAssistantActionResult BeginAuthorization() =>
        new(true, "已生成授权链接", "http://192.168.1.10:8123/auth/authorize?response_type=code&state=abc");

    public Task<MyAiAssistant.Services.HomeAssistantActionResult> CompleteAuthorizationAsync(
        string callback, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MyAiAssistant.Services.HomeAssistantActionResult(true, "授权成功"));

    public Task<MyAiAssistant.Services.HomeAssistantActionResult> UseAccessTokenAsync(
        string token, CancellationToken cancellationToken = default)
    {
        LastToken = token;
        return Task.FromResult(new MyAiAssistant.Services.HomeAssistantActionResult(true, "已使用访问令牌连接"));
    }

    public Task<MyAiAssistant.Services.HomeAssistantActionResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MyAiAssistant.Services.HomeAssistantActionResult(true, "已连接"));

    public MyAiAssistant.Services.HomeAssistantActionResult SignOut()
    {
        SignedOut = true;
        return new MyAiAssistant.Services.HomeAssistantActionResult(true, "已登出");
    }

    public Task<MyAiAssistant.Services.HomeAssistantActionResult> CallServiceAsync(
        string domain, string service, string? entityId, string? dataJson, CancellationToken cancellationToken = default)
    {
        LastCall = $"{domain}.{service}|{entityId}";
        return Task.FromResult(new MyAiAssistant.Services.HomeAssistantActionResult(true, "已执行"));
    }
}
