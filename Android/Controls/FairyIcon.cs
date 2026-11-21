using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace FairyAI_Android.Controls;

/// <summary>
/// 标签栏里的一个图标。用 <see cref="Microsoft.Maui.Graphics.PathF"/> 画矢量图形。
///
/// 为什么不用 SVG 文件：MAUI 自带的 Image 只认 PNG/JPEG，认 SVG 要另加一个包
/// （Microsoft.Maui.Controls.Svg），而这里的图标一共四个、形状也不复杂，
/// 直接用 PathF 画反而更省 —— 而且线条和填充能跟着主题一起换色。
///
/// 画法：所有图形都在 24×24 的坐标系里写，再按实际尺寸整体缩放，
/// 这样图标在不同 dpi 下都保持同一套比例。
/// </summary>
public sealed class FairyIcon : GraphicsView, IDrawable
{
    // GraphicsView 才有 handler 和真正会被调用的 IDrawable；裸 View 只是挂着这个方法，
    // 两者都不到位：没有 handler 会抛 HandlerNotFoundException，Draw 也永远不会被调用。
    public FairyIcon()
    {
        Drawable = this;
    }

    public enum IconKind
    {
        Chat,
        Dashboard,
        Tasks,
        Settings,
    }

    public static readonly BindableProperty KindProperty = BindableProperty.Create(
        nameof(Kind), typeof(IconKind), typeof(FairyIcon), IconKind.Chat,
        propertyChanged: (b, _, _) => ((FairyIcon)b).Invalidate());

    public static readonly BindableProperty TintProperty = BindableProperty.Create(
        nameof(Tint), typeof(Color), typeof(FairyIcon), Colors.White,
        propertyChanged: (b, _, _) => ((FairyIcon)b).Invalidate());

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>线宽（24×24 坐标系里）。</summary>
    private const float Stroke = 1.7f;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var tint = Tint;
        var width = dirtyRect.Width > 0 ? dirtyRect.Width : (float)Width;
        var height = dirtyRect.Height > 0 ? dirtyRect.Height : (float)Height;
        var size = Math.Min(width, height);
        if (size <= 0) return;

        // 画在 24×24 的坐标系里，再整体缩放到实际尺寸。
        var scale = (float)(size / 24.0);
        canvas.SaveState();
        canvas.Translate(dirtyRect.X + (width - size) / 2, dirtyRect.Y + (height - size) / 2);
        canvas.Scale(scale, scale);

        canvas.StrokeSize = Stroke;
        canvas.StrokeColor = tint;
        canvas.FillColor = tint;
        canvas.Antialias = true;

        switch (Kind)
        {
            case IconKind.Chat:
                DrawChat(canvas);
                break;
            case IconKind.Dashboard:
                DrawDashboard(canvas);
                break;
            case IconKind.Tasks:
                DrawTasks(canvas);
                break;
            case IconKind.Settings:
                DrawSettings(canvas);
                break;
        }

        canvas.RestoreState();
    }

    /// <summary>对话：一个气泡轮廓 + 三个实心圆点。</summary>
    private static void DrawChat(ICanvas canvas)
    {
        var bubble = new PathF();
        bubble.MoveTo(6.5f, 3.5f);
        bubble.LineTo(17.5f, 3.5f);
        bubble.AddArc(17.5f, 3.5f, 3f, 3f, 0, 90, true);
        bubble.LineTo(20.5f, 12.5f);
        bubble.AddArc(17.5f, 12.5f, 3f, 3f, 90, 90, true);
        bubble.LineTo(9f, 15.5f);
        bubble.LineTo(5f, 19f);
        bubble.LineTo(5f, 15.5f);
        bubble.AddArc(3.5f, 12.5f, 1.5f, 3f, 180, 90, true);
        bubble.LineTo(3.5f, 6.5f);
        bubble.AddArc(6.5f, 6.5f, 3f, 3f, 270, 90, true);
        bubble.Close();
        canvas.DrawPath(bubble);

        foreach (var x in new[] { 8.5f, 12f, 15.5f })
        {
            canvas.FillCircle(x, 9.5f, 1.25f);
        }
    }

    /// <summary>看板：四格，右下那格填充。</summary>
    private static void DrawDashboard(ICanvas canvas)
    {
        void Box(float x, float y, bool filled)
        {
            const float r = 2.3f;
            const float s = 7.5f;
            var path = new PathF();
            path.MoveTo(x + r, y);
            path.LineTo(x + s - r, y);
            path.AddArc(x + s - r, y + r, r, r, 270, 90, true);
            path.LineTo(x + s, y + s - r);
            path.AddArc(x + s - r, y + s - r, r, r, 0, 90, true);
            path.LineTo(x + r, y + s);
            path.AddArc(x + r, y + s - r, r, r, 90, 90, true);
            path.LineTo(x, y + r);
            path.AddArc(x + r, y + r, r, r, 180, 90, true);
            path.Close();

            if (filled) canvas.FillPath(path);
            else canvas.DrawPath(path);
        }

        Box(3f, 3f, false);
        Box(13.5f, 3f, false);
        Box(3f, 13.5f, false);
        Box(13.5f, 13.5f, true);
    }

    /// <summary>任务：三条清单线 + 两个勾 + 一个实心点。</summary>
    private static void DrawTasks(ICanvas canvas)
    {
        canvas.DrawLine(9.5f, 6f, 20.5f, 6f);
        canvas.DrawLine(9.5f, 12f, 20.5f, 12f);
        canvas.DrawLine(9.5f, 18f, 20.5f, 18f);

        void Tick(float y)
        {
            var tick = new PathF();
            tick.MoveTo(3.6f, y);
            tick.LineTo(4.8f, y + 1.3f);
            tick.LineTo(6.9f, y - 1.1f);
            canvas.StrokeSize = Stroke + 0.2f;
            canvas.DrawPath(tick);
        }

        Tick(6.2f);
        Tick(12.2f);

        canvas.FillCircle(5.2f, 18f, 1.9f);
    }

    /// <summary>设置：齿轮轮廓 + 实心中心。</summary>
    private static void DrawSettings(ICanvas canvas)
    {
        const float cx = 12f, cy = 12f, r = 7.6f, inner = 5.5f;

        var gear = new PathF();
        var first = true;
        for (var i = 0; i < 8; i++)
        {
            var a = Math.PI * 2 / 8 * i;
            var a1 = a - 0.34f;
            var a2 = a + 0.34f;

            var p1 = new PointF(cx + (float)(Math.Cos(a1) * r), cy + (float)(Math.Sin(a1) * r));
            var q1 = new PointF(cx + (float)(Math.Cos(a1) * inner), cy + (float)(Math.Sin(a1) * inner));
            var q2 = new PointF(cx + (float)(Math.Cos(a2) * inner), cy + (float)(Math.Sin(a2) * inner));
            var p2 = new PointF(cx + (float)(Math.Cos(a2) * r), cy + (float)(Math.Sin(a2) * r));

            if (first)
            {
                gear.MoveTo(p1);
                first = false;
            }
            else
            {
                gear.LineTo(p1);
            }
            gear.LineTo(q1);
            gear.AddArc(q1, q2, 0, 90, true);
            gear.LineTo(p2);
        }
        gear.Close();
        canvas.DrawPath(gear);

        canvas.FillCircle(12f, 12f, 2.1f);
    }
}
