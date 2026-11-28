using System.Text.RegularExpressions;

namespace MyAiAssistant.Services;

/// <summary>
/// Parses the text of <c>ollama list</c>.
///
/// The output is a table with padded columns, but it is not only a table: the CLI warns on stderr,
/// prints spinner control codes when a terminal is attached, and on failure prints prose instead of
/// rows. The previous parser assumed "skip the first line, take the first word of the rest", which
/// turns a warning into a model name and an error message into a fake model.
///
/// Rules here are deliberately conservative: a line counts only if its first column looks like an
/// Ollama model reference, so anything that is not a row is dropped rather than guessed at.
/// </summary>
public static partial class OllamaListParser
{
    public static IReadOnlyList<string> Parse(string? output)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return models;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in output.Split('\n'))
        {
            var line = Ansi().Replace(raw, "").Trim();
            if (line.Length == 0) continue;

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // The header row starts with NAME; every table ends with at least ID and SIZE too.
            if (columns.Length >= 2 && columns[0].Equals("NAME", StringComparison.Ordinal)) continue;

            // A single column is never a row: prose (an error, a hint) has spaces in it.
            if (columns.Length < 2) continue;

            var name = columns[0];
            if (!LooksLikeModelName(name)) continue;
            if (!seen.Add(name)) continue;

            models.Add(name);
        }

        return models;
    }

    /// <summary>
    /// <c>name</c> or <c>name:tag</c>, as Ollama accepts them. Anything with punctuation that a
    /// model reference cannot contain (colons in the middle, quotes, parentheses) is prose.
    /// </summary>
    private static bool LooksLikeModelName(string candidate) => ModelName().IsMatch(candidate);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9._-]+)?(:[A-Za-z0-9._-]+)?$")]
    private static partial Regex ModelName();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}
