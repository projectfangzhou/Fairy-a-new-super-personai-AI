using System.Net.Http;

namespace FairyAI_Android.Services.Search;

/// <summary>One search hit, kept tiny: title, address, snippet.</summary>
public sealed class SearchResult
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
}

/// <summary>
/// A way to answer a web search. Implementations are ordered and tried in turn, so a paid API is never
/// the only way to get an answer.
/// </summary>
public interface IWebSearchBackend
{
    string Name { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a search engine's plain-HTML endpoint. No key, no quota, no browser — the cheapest possible
/// path, and the fallback when the embedded browser cannot be used.
/// </summary>
public sealed class HtmlSearchBackend : IWebSearchBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Name => "html";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchResult>();

        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36");

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Array.Empty<SearchResult>();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(html, maxResults);
    }

    /// <summary>Extracts the result anchors. Kept separate so it can be checked against a fixture.</summary>
    public static IReadOnlyList<SearchResult> Parse(string html, int maxResults)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(html) || maxResults <= 0) return results;

        foreach (var block in html.Split("result__a", StringSplitOptions.None).Skip(1))
        {
            var href = System.Text.RegularExpressions.Regex.Match(block, "href=\"([^\"]+)\"");
            var title = System.Text.RegularExpressions.Regex.Match(block, ">([^<]{2,})</a>");

            if (!href.Success || !title.Success) continue;

            var link = System.Net.WebUtility.HtmlDecode(href.Groups[1].Value);
            var text = System.Net.WebUtility.HtmlDecode(title.Groups[1].Value).Trim();

            if (text.Length == 0 || link.Length == 0) continue;
            if (results.Any(existing => existing.Url == link)) continue;

            results.Add(new SearchResult { Title = text, Url = link });
            if (results.Count >= maxResults) break;
        }

        return results;
    }
}

/// <summary>
/// Tries each backend in order and reports which one answered. A backend that fails is skipped — the
/// point of the list is that no single provider can make search stop working.
/// </summary>
public sealed class CompositeWebSearch
{
    private readonly IReadOnlyList<IWebSearchBackend> _backends;

    public CompositeWebSearch(IEnumerable<IWebSearchBackend> backends) =>
        _backends = backends?.Where(backend => backend is not null).ToList()
                    ?? throw new ArgumentNullException(nameof(backends));

    public IReadOnlyList<IWebSearchBackend> Backends => _backends;

    /// <summary>Which backend produced the last answer, for the log and for honest reporting.</summary>
    public string? LastUsed { get; private set; }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        foreach (var backend in _backends)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var results = await backend.SearchAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
                if (results.Count == 0) continue;

                LastUsed = backend.Name;
                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try the next one; the caller learns only that nobody answered.
            }
        }

        LastUsed = null;
        return Array.Empty<SearchResult>();
    }
}
