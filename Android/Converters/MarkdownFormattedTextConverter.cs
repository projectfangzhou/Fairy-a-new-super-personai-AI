using System.Globalization;
using FairyAI_Android.Services.Markdown;

namespace FairyAI_Android;

/// <summary>
/// Renders assistant markdown as label spans: bold, italic, monospace inline code and underlined
/// links. Plain labels stay plain — this is only bound to the assistant side of the conversation.
/// </summary>
public sealed class MarkdownFormattedTextConverter : IValueConverter
{
    private static readonly Color BodyColor = Color.FromArgb("#E9E9E9");
    private static readonly Color LinkColor = Color.FromArgb("#7FB3FF");
    private static readonly Color CodeColor = Color.FromArgb("#9FE8C0");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var formatted = new FormattedString();
        var markdown = value as string ?? "";

        foreach (var piece in MarkdownFormatter.Parse(markdown))
        {
            var span = new Span
            {
                Text = piece.Text,
                FontSize = 15,
                TextColor = piece.Code ? CodeColor : piece.Link is null ? BodyColor : LinkColor,
            };

            if (piece.Bold) span.FontAttributes |= FontAttributes.Bold;
            if (piece.Italic) span.FontAttributes |= FontAttributes.Italic;
            if (piece.Code) span.FontFamily = "monospace";
            if (piece.Link is not null) span.TextDecorations = TextDecorations.Underline;

            formatted.Spans.Add(span);
        }

        return formatted;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Markdown is one-way.");
}
