using System.IO;
using FairyAI_Android.Services.Knowledge;

namespace FairyAI_Android.Services;

/// <summary>
/// Android knowledge base: imports documents and makes them findable.
///
/// The import half always worked; what was missing was any way to *use* what had been imported.
/// <see cref="Search"/> now answers from the same .md files the import writes, through a keyword
/// index that is rebuilt only when the folder actually changed.
/// </summary>
public class KnowledgeBaseService : IKnowledgeSearch
{
    private readonly string _kbFolder;
    private readonly object _gate = new();
    private KnowledgeIndex? _index;
    private string _signature = "";
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    public KnowledgeBaseService()
    {
        _kbFolder = Path.Combine(FileSystem.AppDataDirectory, "knowledge");
        Directory.CreateDirectory(_kbFolder);
    }

    public string KbFolder => _kbFolder;

    /// <summary>Names of the imported documents.</summary>
    public IReadOnlyList<string> Documents => ListKnowledge();

    /// <summary>Passages the current index can answer from — zero means nothing to search.</summary>
    public int PassageCount => CurrentIndex().PassageCount;

    /// <summary>Save important extracted info.</summary>
    public async Task SaveImportantInfoAsync(string category, string content)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_kbFolder, $"{category}_{date}.md");
        var entry = $"## {DateTime.Now:HH:mm}\n{content}\n\n";
        await File.AppendAllTextAsync(filePath, entry);
        Invalidate();
    }

    /// <summary>Import a plain text document (the share target and the paste path both use this).</summary>
    public async Task<string> ImportTextAsync(string name, string text)
    {
        var safeName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(name) ? "笔记" : name);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "笔记";
        if (safeName.Length > 80) safeName = safeName[..80];

        var kbFile = Path.Combine(_kbFolder, safeName + ".md");
        await File.WriteAllTextAsync(kbFile, $"# {safeName}\n\n{text}");
        Invalidate();
        Log($"Document imported: {safeName}");
        return kbFile;
    }

    public async Task<string> AnalyzeDocumentAsync(string filePath)
    {
        if (!File.Exists(filePath)) return "文件不存在";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        string text = ext switch
        {
            ".txt" or ".md" => await File.ReadAllTextAsync(filePath),
            ".docx" => await ExtractDocxTextAsync(filePath),
            ".pptx" => await ExtractPptxTextAsync(filePath),
            _ => await File.ReadAllTextAsync(filePath)
        };

        if (string.IsNullOrWhiteSpace(text))
            return $"无法从 {ext} 文件提取文本";

        var kbFile = Path.Combine(_kbFolder, Path.GetFileNameWithoutExtension(filePath) + ".md");
        await File.WriteAllTextAsync(kbFile, $"# {Path.GetFileName(filePath)}\n\n{text}");
        Invalidate();
        Log($"Document analyzed: {Path.GetFileName(filePath)}");
        return text.Length > 2000 ? text[..2000] + "..." : text;
    }

    public List<string> ListKnowledge()
    {
        return Directory.GetFiles(_kbFolder, "*.md")
            .Select(f => Path.GetFileName(f)).OrderBy(f => f).ToList();
    }

    /// <summary>Passages that share words with the query, best first. Empty when nothing matches.</summary>
    public IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 5) => CurrentIndex().Search(query, limit);

    /// <summary>Forces a rebuild on the next search; called after any write to the folder.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index = null;
            _signature = "";
        }
    }

    private KnowledgeIndex CurrentIndex()
    {
        lock (_gate)
        {
            var signature = Signature();
            if (_index is not null && signature == _signature) return _index;

            var documents = new List<KnowledgeDocument>();

            foreach (var file in Directory.GetFiles(_kbFolder, "*.md"))
            {
                try
                {
                    documents.Add(new KnowledgeDocument(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Skipped unreadable document: {Path.GetFileName(file)}");
                }
            }

            _index = KnowledgeIndex.Build(documents);
            _signature = signature;
            Log($"Index rebuilt: {documents.Count} documents, {_index.PassageCount} passages");
            return _index;
        }
    }

    /// <summary>Cheap change detector: name + size + write time of every file in the folder.</summary>
    private string Signature()
    {
        var builder = new System.Text.StringBuilder();

        try
        {
            foreach (var file in Directory.GetFiles(_kbFolder, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                builder.Append(info.Name).Append(':').Append(info.Length).Append(':')
                    .Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }

        return builder.ToString();
    }

    private static async Task<string> ExtractDocxTextAsync(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) return "";
            using var reader = new StreamReader(entry.Open());
            var xml = await reader.ReadToEndAsync();
            return System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ").Trim();
        }
        catch { return ""; }
    }

    private static async Task<string> ExtractPptxTextAsync(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            var sb = new System.Text.StringBuilder();
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide")))
            {
                using var reader = new StreamReader(entry.Open());
                var xml = await reader.ReadToEndAsync();
                sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ").Trim());
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [KB] {msg}\n"); } catch { }
    }
}
