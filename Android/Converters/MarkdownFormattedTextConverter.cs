using System.Globalization;
using FairyAI_Android.Services.Markdown;
using FairyAI_Android.Services.Theme;

namespace FairyAI_Android;

/// <summary>
/// Renders assistant markdown as label spans: bold, italic, monospace inline code and underlined
/// links. Plain labels stay plain — this is only bound to the assistant side of the conversation.
///
/// 颜色从当前主题取（而不是写死），否则换主题后助手消息还是旧颜色，
/// 会和页面其它部分对不上。取不到时退回墨黑的一套。
/// </summary>
public sealed class MarkdownFormattedTextConverter : IValueConverter
{
    private static Color BodyColor => Glass.ReadColor("Theme.TextPrimary");
    private static Color LinkColor => Glass.ReadColor("Theme.Accent");
    private static Color CodeColor => Glass.ReadColor("Theme.Success");

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
