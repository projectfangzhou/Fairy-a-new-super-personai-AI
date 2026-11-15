using System.Text.RegularExpressions;

namespace FairyAI_Android.Services.Markdown;

/// <summary>One styled run of assistant text. The UI turns these into spans; nothing here knows about MAUI.</summary>
public sealed record MarkdownSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

/// <summary>
/// Turns assistant markdown into styled runs. Deliberately small: bold, italic, inline code, links,
/// headings, list markers and fenced code. Anything else is shown as written — a chat bubble should
/// never lose text because the parser did not recognise a construct.
/// </summary>
public static class MarkdownFormatter
{
    private static readonly Regex Inline = new(
        @"(?<bold>\*\*(?<boldText>.+?)\*\*)" +
        @"|(?<code>`(?<codeText>[^`]+)`)" +
        @"|(?<italic>\*(?<italicText>[^*]+)\*)" +
        @"|(?<link>\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)\))",
        RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^(?<hashes>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex ListItem = new(@"^\s*(?:[-*+]|\d+\.)\s+(?<text>.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<MarkdownSpan> Parse(string? markdown)
    {
        var spans = new List<MarkdownSpan>();
        if (string.IsNullOrEmpty(markdown)) return spans;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        var fence = new System.Text.StringBuilder();
        var first = true;

        void AddLineBreak()
        {
            if (!first) spans.Add(new MarkdownSpan("\n"));
            first = false;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    AddLineBreak();
                    var code = fence.ToString().TrimEnd('\n');
                    if (code.Length > 0) spans.Add(new MarkdownSpan(code, Code: true));
                    fence.Clear();
                }

                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                fence.Append(rawLine).Append('\n');
                continue;
            }

            AddLineBreak();

            var heading = Heading.Match(rawLine);
            if (heading.Success)
            {
                AddInline(spans, heading.Groups["text"].Value, forceBold: true);
                continue;
            }

            var item = ListItem.Match(rawLine);
            if (item.Success)
            {
                spans.Add(new MarkdownSpan("• "));
                AddInline(spans, item.Groups["text"].Value);
                continue;
            }

            if (rawLine.Length == 0) continue;
            AddInline(spans, rawLine);
        }

        if (inFence && fence.Length > 0)
        {
            AddLineBreak();
            spans.Add(new MarkdownSpan(fence.ToString().TrimEnd('\n'), Code: true));
        }

        return spans;
    }

    /// <summary>Everything the parser understood, as text — used to check nothing was dropped.</summary>
    public static string ToPlainText(string? markdown) =>
        string.Concat(Parse(markdown).Select(span => span.Text));

    private static void AddInline(List<MarkdownSpan> spans, string text, bool forceBold = false)
    {
        if (text.Length == 0) return;

        var position = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > position)
                spans.Add(new MarkdownSpan(text[position..match.Index], Bold: forceBold));

            if (match.Groups["bold"].Success)
                spans.Add(new MarkdownSpan(match.Groups["boldText"].Value, Bold: true));
            else if (match.Groups["code"].Success)
                spans.Add(new MarkdownSpan(match.Groups["codeText"].Value, Code: true));
            else if (match.Groups["italic"].Success)
                spans.Add(new MarkdownSpan(match.Groups["italicText"].Value, Italic: true, Bold: forceBold));
            else if (match.Groups["link"].Success)
                spans.Add(new MarkdownSpan(
                    match.Groups["linkText"].Value,
                    Bold: forceBold,
                    Link: match.Groups["linkUrl"].Value));

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            spans.Add(new MarkdownSpan(text[position..], Bold: forceBold));
    }
}
