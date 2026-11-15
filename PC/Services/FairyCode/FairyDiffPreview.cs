using System.IO;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.FairyCode;

/// <summary>
/// Builds the change preview shown with an approval request. Approving a file edit without
/// seeing what changes is not consent, it is a guess — so the preview is part of the approval
/// prompt itself, not a separate screen the user might never open.
///
/// Everything here is bounded: a huge file must not turn an approval prompt into megabytes,
/// and a file that cannot be read must say so instead of showing an empty diff.
/// </summary>
public static class FairyDiffPreview
{
    private const int MaxPreviewLines = 80;
    private const int MaxSourceLines = 4000;
    private const int ContextLines = 2;
    private const int MaxContentLength = 4 * 1024 * 1024;

    public sealed record Preview(string Text, bool IsComplete);

    /// <summary>
    /// Returns the preview for a write or edit call, or null when the call is not a file
    /// mutation (or its shape is not understood — in that case no preview is better than a
    /// misleading one).
    /// </summary>
    public static Preview? Build(EngineToolCall call, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(call);
        var tool = call.ToolName.Trim().ToLowerInvariant();
        if (tool is not ("write" or "edit")) return null;

        var relative = call.Argument("path") ?? call.Argument("file_path") ?? call.Argument("filePath");
        if (string.IsNullOrWhiteSpace(relative)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathFullyQualified(relative)
                ? relative
                : Path.Combine(workspaceRoot, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var current = TryRead(full, out var unreadableReason);
        var proposed = tool == "write"
            ? call.Argument("content")
            : ApplyEdits(current, call.Argument("edits"));

        if (proposed is null)
            return new Preview($"（无法解析这次修改的内容，只显示目标：{full}）", true);

        if (proposed.Length > MaxContentLength)
            return new Preview($"（新内容超过 4 MB，不预览；目标：{full}）", true);

        if (current is null && unreadableReason is not null)
            return new Preview($"（无法读取现有文件：{unreadableReason}。批准后仍会写入）", true);

        var diff = Unified(current ?? string.Empty, proposed, current is null, full);
        return diff;
    }

    /// <summary>
    /// Applies pi's edit shape ({edits:[{oldText,newText}]}). Each oldText is matched against
    /// the file as it was before the call, which is what the tool itself documents.
    /// </summary>
    internal static string? ApplyEdits(string? current, string? editsJson)
    {
        if (current is null || string.IsNullOrWhiteSpace(editsJson)) return null;

        var edits = ParseEdits(editsJson);
        if (edits is null || edits.Count == 0) return null;

        var result = current;
        foreach (var edit in edits)
        {
            if (string.IsNullOrEmpty(edit.OldText) || edit.NewText is null) return null;
            var index = result.IndexOf(edit.OldText, StringComparison.Ordinal);
            // A mismatch means the preview would be fiction; refuse to show one.
            if (index < 0) return null;
            result = result[..index] + edit.NewText + result[(index + edit.OldText.Length)..];
        }
        return result;
    }

    private static List<EditEntry>? ParseEdits(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("edits", out var nested))
                root = nested;

            if (root.ValueKind != JsonValueKind.Array) return null;

            var edits = new List<EditEntry>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return null;
                var oldText = element.TryGetProperty("oldText", out var oldValue) &&
                              oldValue.ValueKind == JsonValueKind.String
                    ? oldValue.GetString()
                    : null;
                var newText = element.TryGetProperty("newText", out var newValue) &&
                              newValue.ValueKind == JsonValueKind.String
                    ? newValue.GetString()
                    : null;
                edits.Add(new EditEntry(oldText, newText));
            }
            return edits;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record EditEntry(string? OldText, string? NewText);

    private static string? TryRead(string path, out string? reason)
    {
        reason = null;
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024)
            {
                reason = "文件超过 4 MB";
                return null;
            }
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.GetType().Name;
            return null;
        }
    }

    private static Preview Unified(string before, string after, bool isNewFile, string fullPath)
    {
        if (before == after)
            return new Preview($"（内容没有变化：{fullPath}）", true);

        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        if (oldLines.Length > MaxSourceLines || newLines.Length > MaxSourceLines)
        {
            return new Preview(
                $"（文件过大，不逐行比对：{oldLines.Length} 行 → {newLines.Length} 行，" +
                $"原 {before.Length} 字符 / 新 {after.Length} 字符）", true);
        }

        var hunks = BuildHunks(oldLines, newLines);
        var builder = new StringBuilder();
        builder.AppendLine(isNewFile ? "新建文件" : "修改文件");
        builder.AppendLine(fullPath);

        var lines = 0;
        var complete = true;
        foreach (var line in hunks)
        {
            if (lines >= MaxPreviewLines)
            {
                complete = false;
                break;
            }
            builder.AppendLine(line);
            lines++;
        }

        if (!complete)
            builder.AppendLine($"… 预览已截断（共 {hunks.Count} 行差异）");

        return new Preview(builder.ToString().TrimEnd(), complete);
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// A compact line diff. LCS is used on the whole file because the preview has to be
    /// trustworthy; files above <see cref="MaxSourceLines"/> are not compared at all.
    /// </summary>
    internal static List<string> BuildHunks(string[] oldLines, string[] newLines)
    {
        var ops = Diff(oldLines, newLines);
        var output = new List<string>();

        // Group operations into hunks with context, skipping long unchanged stretches.
        var index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Kind == DiffKind.Same)
            {
                index++;
                continue;
            }

            var start = Math.Max(0, index - ContextLines);
            var end = index;
            var sameRun = 0;
            while (end < ops.Count)
            {
                if (ops[end].Kind == DiffKind.Same)
                {
                    sameRun++;
                    if (sameRun > ContextLines * 2) break;
                }
                else
                {
                    sameRun = 0;
                }
                end++;
            }

            var hunkEnd = Math.Min(ops.Count, end);
            var oldCount = ops.Skip(start).Take(hunkEnd - start).Count(o => o.Kind != DiffKind.Add);
            var newCount = ops.Skip(start).Take(hunkEnd - start).Count(o => o.Kind != DiffKind.Remove);
            var oldStart = ops.Take(start).Count(o => o.Kind != DiffKind.Add) + 1;
            var newStart = ops.Take(start).Count(o => o.Kind != DiffKind.Remove) + 1;

            output.Add($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");
            for (var i = start; i < hunkEnd; i++)
            {
                var prefix = ops[i].Kind switch
                {
                    DiffKind.Add => "+",
                    DiffKind.Remove => "-",
                    _ => " "
                };
                output.Add(prefix + ops[i].Text);
            }

            index = hunkEnd;
        }

        return output;
    }

    private enum DiffKind { Same, Add, Remove }
    private readonly record struct DiffOp(DiffKind Kind, string Text);

    private static List<DiffOp> Diff(string[] a, string[] b)
    {
        // Classic LCS table; the caller already refused files that would make this expensive.
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var ops = new List<DiffOp>(a.Length + b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                ops.Add(new DiffOp(DiffKind.Same, a[x]));
                x++; y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                ops.Add(new DiffOp(DiffKind.Remove, a[x]));
                x++;
            }
            else
            {
                ops.Add(new DiffOp(DiffKind.Add, b[y]));
                y++;
            }
        }
        while (x < a.Length) ops.Add(new DiffOp(DiffKind.Remove, a[x++]));
        while (y < b.Length) ops.Add(new DiffOp(DiffKind.Add, b[y++]));
        return ops;
    }
}
