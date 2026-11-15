using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyAiAssistant.Windows;

namespace IconPreview;

/// <summary>
/// Renders every SVG in Assets/Dock into one contact sheet so the artwork can be eyeballed
/// and compared before it ships in the rail.
/// </summary>
internal static class Program
{
    private const int Cell = 104;
    private const int Columns = 6;

    [STAThread]
    private static int Main(string[] args)
    {
        var assets = args.Length > 0 ? args[0] : FindAssets();
        if (assets is null || !Directory.Exists(assets))
        {
            Console.Error.WriteLine("Assets/Dock not found");
            return 1;
        }

        var files = Directory.GetFiles(assets, "*.svg")
            .Where(f => !Path.GetFileName(f).Equals("example.svg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

        var rows = (int)Math.Ceiling(files.Length / (double)Columns);
        var width = Columns * Cell;
        var height = rows * (Cell + 22) + 16;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x2E)), null,
                new Rect(0, 0, width, height));

            var stroke = new SolidColorBrush(Color.FromRgb(0xC9, 0xC7, 0xD8));
            for (var i = 0; i < files.Length; i++)
            {
                var name = Path.GetFileNameWithoutExtension(files[i]);
                var icon = DockIconLoader.Load(assets, name, "M0,0");
                if (icon.Vector is null)
                {
                    Console.Error.WriteLine($"{name}: no outline");
                    continue;
                }

                var bb = icon.Vector.Bounds;
                Console.WriteLine($"{name,-16} x={bb.Left,5:0.0} y={bb.Top,5:0.0} w={bb.Width,5:0.0} h={bb.Height,5:0.0}");
                var column = i % Columns;
                var row = i / Columns;
                var originX = column * Cell;
                var originY = row * (Cell + 22) + 8;

                // Fit 24-unit artwork into a 64px box, then scale the stroke with it.
                const double iconBox = 64;
                var scale = iconBox / 24.0;
                var offsetX = originX + (Cell - iconBox) / 2;
                var offsetY = originY + (Cell - iconBox) / 2 - 4;

                dc.PushTransform(new TranslateTransform(offsetX, offsetY));
                dc.PushTransform(new ScaleTransform(scale, scale));
                var pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, pen, icon.Vector);
                dc.Pop();
                dc.Pop();

                var label = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 11, new SolidColorBrush(Color.FromRgb(0x8A, 0x88, 0xA0)),
                    VisualTreeHelper.GetDpi(visual).PixelsPerDip);
                dc.DrawText(label, new Point(originX + (Cell - label.Width) / 2, originY + Cell + 2));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var output = Path.Combine(Path.GetTempPath(), "fairyai-icon-preview.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);

        Console.WriteLine(output);
        return 0;
    }

    private static string? FindAssets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "Dock");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}