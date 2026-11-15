// Important info extraction.
// Ported from PC MyAiAssistant/Services/ImportantInfoService.cs

using System.IO;
using FairyAI_Android.Services.Knowledge;

namespace FairyAI_Android.Services;

/// <summary>
/// Finds dates, phone numbers and addresses in a piece of text and saves them as a dated note in the
/// knowledge folder, where the knowledge search can find them again.
///
/// The patterns live in <see cref="ImportantInfoExtractor"/> so they can be asserted without a device.
/// Called from the screen-look flow: what the vision model read off the screen often contains exactly
/// these details.
/// </summary>
public class ImportantInfoService
{
    private readonly KnowledgeBaseService _kb;
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    public ImportantInfoService(KnowledgeBaseService kb) => _kb = kb;

    /// <summary>Extract and save important info (dates, addresses, phone numbers).</summary>
    public async Task<string> ExtractAndSaveAsync(string text)
    {
        var extracted = ImportantInfoExtractor.Extract(text);
        if (extracted.Count == 0) return "没有找到日期、电话或地址";

        var content = string.Join("\n", extracted);
        await _kb.SaveImportantInfoAsync("auto_extracted", content);
        Log($"Extracted {extracted.Count} items");
        return content;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [INFO] {msg}\n"); } catch { }
    }
}
