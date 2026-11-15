using System.IO;
using System.Text;
using FairyAI_Android.Services.Search;

namespace FairyAI_Android.Services;

public interface ISearchService
{
    Task<string> SearchAsync(string query);
}

/// <summary>
/// Search for the assistant. The order is fixed and the first backend that answers wins: the embedded
/// Chromium engine first because it costs nothing, then the plain-HTML endpoint, which also costs
/// nothing. Neither needs an API key, so search works on a fresh install.
/// </summary>
public class SearchService : ISearchService
{
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    private readonly CompositeWebSearch _search;

    public SearchService(CompositeWebSearch search) =>
        _search = search ?? throw new ArgumentNullException(nameof(search));

    public async Task<string> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "没有给出搜索关键词";

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _search.SearchAsync(query, 5).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"search failed: {ex.GetType().Name}");
            return "这次搜索出错了，稍后再试。";
        }

        if (results.Count == 0)
        {
            Log($"search found nothing (backends: {string.Join(", ", _search.Backends.Select(b => b.Name))})");
            return "这次没搜到结果：内嵌浏览器和网页直连两条路都没有返回内容。";
        }

        Log($"search '{query}' answered by {_search.LastUsed}: {results.Count} hits");

        var builder = new StringBuilder();
        foreach (var result in results)
        {
            builder.AppendLine($"**{result.Title}**");
            builder.AppendLine(result.Url);
            if (!string.IsNullOrWhiteSpace(result.Snippet)) builder.AppendLine(result.Snippet);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [SEARCH] {message}\n");
        }
        catch
        {
            // Logging must never break search.
        }
    }
}
