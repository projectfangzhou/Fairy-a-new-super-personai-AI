using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyAiAssistant.Windows;

public sealed record DockIcon(Geometry? Vector, ImageSource? Bitmap, bool FromFile)
{
    public bool IsUsable => Vector is not null || Bitmap is not null;
}

/// <summary>
/// Loads dock icons from <c>Assets/Dock</c> so artwork can be dropped in without touching code.
/// A <c>{key}.svg</c> wins, then <c>{key}.png</c>, then the vector compiled into the app.
/// Only the path/shape outline is taken from the SVG: colour, weight and state come from the
/// dock itself, which is what keeps the rail consistent.
/// </summary>
public static partial class DockIconLoader
{
    public static string IconDirectory =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Dock");

    public static DockIcon Load(string key, string builtInVector) => Load(IconDirectory, key, builtInVector);

    public static DockIcon Load(string directory, string key, string builtInVector)
    {
        try
        {
            var svgPath = Path.Combine(directory, key + ".svg");
            if (File.Exists(svgPath))
            {
                var geometry = ParseSvgOutline(File.ReadAllText(svgPath));
                if (geometry is not null) return new DockIcon(geometry, null, true);
            }

            var pngPath = Path.Combine(directory, key + ".png");
            if (File.Exists(pngPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                // OnLoad keeps the file closed so artwork can be replaced while the app runs.
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(pngPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return new DockIcon(null, bitmap, true);
            }
        }
        catch (Exception)
        {
            // A broken or unreadable file must not take the dock down; fall through to the
            // compiled-in outline.
        }

        return new DockIcon(Geometry.Parse(builtInVector), null, false);
    }

    /// <summary>
    /// Builds a geometry from the drawable shapes in an SVG. Transforms are intentionally not
    /// applied — artwork should be supplied with transforms already baked in.
    /// </summary>
    public static Geometry? ParseSvgOutline(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return null;

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        var found = false;

        foreach (Match match in PathDataPattern().Matches(svg))
        {
            var data = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(data)) continue;
            try
            {
                group.Children.Add(Geometry.Parse(data));
                found = true;
            }
            catch (Exception) { /* skip a shape we cannot read rather than dropping the whole icon */ }
        }

        foreach (Match match in CirclePattern().Matches(svg))
        {
            if (!TryReadAttribute(match.Value, "cx", out var cx) ||
                !TryReadAttribute(match.Value, "cy", out var cy) ||
                !TryReadAttribute(match.Value, "r", out var r)) continue;

            group.Children.Add(new EllipseGeometry(new System.Windows.Point(cx, cy), r, r));
            found = true;
        }

        return found ? group : null;
    }

    private static bool TryReadAttribute(string element, string name, out double value)
    {
        value = 0;
        var match = Regex.Match(element, name + @"\s*=\s*""([\-0-9.]+)""", RegexOptions.IgnoreCase);
        return match.Success &&
               double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"<path\b[^>]*\bd\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathDataPattern();

    [GeneratedRegex(@"<circle\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex CirclePattern();
}