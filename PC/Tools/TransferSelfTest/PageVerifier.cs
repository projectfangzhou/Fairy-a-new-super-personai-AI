using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TransferSelfTest;

/// <summary>
/// Opens the phone page in WebView2 and drives it like a person would: wait for the transfer offer,
/// press "allow", then wait for the hash check to pass.
///
/// WebView2 is the same Chromium engine a phone's browser uses, so this is the closest thing to a
/// phone run that can happen on one machine. The certificate is ignored on purpose — the page only
/// needs to be secure enough for the browser to expose its crypto API.
/// </summary>
internal sealed class PageVerifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly WebView2 _view;
    private readonly Window _window;
    private readonly TestLog _log;
    private readonly TaskCompletionSource<bool> _passed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PageVerifier(WebView2 view, Window window, TestLog log)
    {
        _view = view;
        _window = window;
        _log = log;
    }

    public static async Task<PageVerifier> StartAsync(string url, TestLog log)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(AppContext.BaseDirectory, "webview2-pagecheck"),
            new CoreWebView2EnvironmentOptions("--ignore-certificate-errors"));

        var window = new Window
        {
            Width = 420,
            Height = 760,
            Left = -3000,
            Top = -3000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Title = "页面自检"
        };

        var view = new WebView2();
        window.Content = view;
        window.Show();

        await view.EnsureCoreWebView2Async(environment);

        // The certificate is generated per run and cannot be trusted in advance; accepting it here is
        // the programmatic equivalent of the warning a phone shows.
        view.CoreWebView2.ServerCertificateErrorDetected += (_, args) =>
            args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;

        view.CoreWebView2.NavigationCompleted += (_, args) =>
            log.Info(args.IsSuccess
                ? $"页面加载完成：{url}"
                : $"页面加载失败：{args.WebErrorStatus}（{url}）");

        view.CoreWebView2.Navigate(url);

        var verifier = new PageVerifier(view, window, log);
        log.Info("已在 WebView2 中打开页面：" + url);
        _ = Task.Run(verifier.LoopAsync);
        return verifier;
    }

    public async Task<bool> PassAsync(TestLog log)
    {
        var finished = await Task.WhenAny(_passed.Task, Task.Delay(Timeout));
        var ok = finished == _passed.Task && _passed.Task.Result;

        if (!ok) log.Fail("页面没有报告校验通过（页面状态见上面的日志）");

        try { _window.Close(); } catch (Exception) { /* already gone */ }
        return ok;
    }

    private async Task LoopAsync()
    {
        var accepted = false;
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline && !_passed.Task.IsCompleted)
        {
            try
            {
                var scripted = await _view.Dispatcher.InvokeAsync(() => _view.ExecuteScriptAsync("""
                    JSON.stringify({
                      url: location.href,
                      ready: document.readyState,
                      secure: location.protocol === 'https:',
                      connected: typeof SimpleCrypto !== 'undefined',
                      state: (document.getElementById('state') || {}).textContent || '',
                      offer: !!document.getElementById('offer') && !document.getElementById('offer').hidden,
                      result: (document.getElementById('result') || {}).textContent || ''
                    })
                    """));

                var raw = await scripted;
                var json = JsonSerializer.Deserialize<string>(raw ?? "null");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    var pageUrl = root.GetProperty("url").GetString() ?? string.Empty;
                    var readyState = root.GetProperty("ready").GetString() ?? string.Empty;
                    var connected = root.GetProperty("connected").GetBoolean();
                    var state = root.GetProperty("state").GetString() ?? string.Empty;
                    var offer = root.GetProperty("offer").GetBoolean();
                    var result = root.GetProperty("result").GetString() ?? string.Empty;

                    // Only judge the crypto API once our page is actually the loaded document.
                    if (readyState == "complete" && pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !connected)
                    {
                        _log.Fail("页面已加载但没有加载到加密脚本：" + pageUrl);
                        _passed.TrySetResult(false);
                        return;
                    }

                    if (offer && !accepted)
                    {
                        _log.Ok("页面收到传输请求，自动点「允许接收」");
                        await _view.Dispatcher.InvokeAsync(() =>
                            _view.ExecuteScriptAsync("document.getElementById('accept').click()"));
                        accepted = true;
                    }

                    if (result.Contains("校验通过", StringComparison.Ordinal))
                    {
                        _log.Ok("页面侧校验通过：" + result.Replace('\n', ' '));
                        _passed.TrySetResult(true);
                        return;
                    }

                    if (result.Contains("丢弃", StringComparison.Ordinal) ||
                        state.Contains("拒绝", StringComparison.Ordinal))
                    {
                        _log.Fail("页面侧没有通过：" + result);
                        _passed.TrySetResult(false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Info("页面轮询异常（可忽略一次）：" + ex.GetType().Name);
            }

            await Task.Delay(200);
        }

        _passed.TrySetResult(false);
    }
}
