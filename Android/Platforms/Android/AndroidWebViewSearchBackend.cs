#if ANDROID
using System.Text.Json;
using FairyAI_Android.Services.Search;

namespace FairyAI_Android.Services.Search;

/// <summary>
/// Search through the Chromium engine Android already ships (WebView): load the plain-HTML search page
/// and read the result nodes out of the DOM after it finishes.
///
/// This is the cheap path — no search API, no key, no quota. It is also the brittle one: if the engine
/// changes its markup the extraction returns nothing, which is why <see cref="CompositeWebSearch"/> has
/// another backend after it and the caller is told which one answered.
///
/// A WebView must be created and driven on the UI thread, so the work is marshalled there and the
/// caller awaits a task.
/// </summary>
public sealed class AndroidWebViewSearchBackend : IWebSearchBackend
{
    private const int TimeoutSeconds = 20;

    private const string ExtractScript = """
        (function () {
          var out = [];
          var links = document.querySelectorAll('a.result__a');
          for (var i = 0; i < links.length; i++) {
            var anchor = links[i];
            var block = anchor.closest ? anchor.closest('.result') : null;
            var snippet = block ? block.querySelector('.result__snippet') : null;
            out.push({
              title: (anchor.innerText || '').trim(),
              url: anchor.href || '',
              snippet: snippet ? (snippet.innerText || '').trim() : ''
            });
          }
          return JSON.stringify(out);
        })()
        """;

    public string Name => "webview";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return Array.Empty<SearchResult>();

        var completion = new TaskCompletionSource<IReadOnlyList<SearchResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Android.Webkit.WebView? webView = null;

        void Finish(IReadOnlyList<SearchResult> results)
        {
            if (!completion.TrySetResult(results)) return;

            var view = webView;
            webView = null;
            try
            {
                view?.Destroy();
                view?.Dispose();
            }
            catch
            {
                // Disposal must not mask the result.
            }
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                webView = new Android.Webkit.WebView(Android.App.Application.Context);
                var settings = webView.Settings;
                if (settings is not null)
                {
                    settings.JavaScriptEnabled = true;
                    settings.DomStorageEnabled = true;
                }

                webView.SetWebViewClient(new PageClient(maxResults, ExtractScript, Finish));
                webView.LoadUrl($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
            }
            catch (Exception)
            {
                Finish(Array.Empty<SearchResult>());
            }
        });

        var timeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken);
        var finished = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);

        if (finished == completion.Task)
            return await completion.Task.ConfigureAwait(false);

        // Timed out (or cancelled): tear the view down and report nothing rather than hanging.
        Finish(Array.Empty<SearchResult>());
        return Array.Empty<SearchResult>();
    }

    private sealed class PageClient : Android.Webkit.WebViewClient
    {
        private readonly int _maxResults;
        private readonly string _script;
        private readonly Action<IReadOnlyList<SearchResult>> _finish;
        private bool _handled;

        public PageClient(int maxResults, string script, Action<IReadOnlyList<SearchResult>> finish)
        {
            _maxResults = maxResults;
            _script = script;
            _finish = finish;
        }

        public override void OnPageFinished(Android.Webkit.WebView? view, string? url)
        {
            base.OnPageFinished(view, url);

            if (_handled || view is null)
            {
                _finish(Array.Empty<SearchResult>());
                return;
            }

            _handled = true;

            try
            {
                view.EvaluateJavascript(_script, new ValueCallback(raw =>
                {
                    try
                    {
                        _finish(ParseEvaluatedJson(raw, _maxResults));
                    }
                    catch (Exception)
                    {
                        _finish(Array.Empty<SearchResult>());
                    }
                }));
            }
            catch (Exception)
            {
                _finish(Array.Empty<SearchResult>());
            }
        }

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request) => true;
    }

    /// <summary>
    /// EvaluateJavascript hands back a JSON-encoded string, so the payload is decoded twice: once to get
    /// the JSON text the page produced, once to read the array.
    /// </summary>
    public static IReadOnlyList<SearchResult> ParseEvaluatedJson(string? evaluated, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(evaluated) || evaluated == "null") return Array.Empty<SearchResult>();

        var json = JsonSerializer.Deserialize<string>(evaluated);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SearchResult>();

        var parsed = JsonSerializer.Deserialize<List<SearchResult>>(json);
        return parsed is null ? Array.Empty<SearchResult>() : parsed.Take(maxResults).ToList();
    }

    private sealed class ValueCallback : Java.Lang.Object, Android.Webkit.IValueCallback
    {
        private readonly Action<string?> _onValue;

        public ValueCallback(Action<string?> onValue) => _onValue = onValue;

        public void OnReceiveValue(Java.Lang.Object? value) => _onValue(value?.ToString());
    }
}
#endif
