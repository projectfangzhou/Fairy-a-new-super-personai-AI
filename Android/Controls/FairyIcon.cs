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
/// 画法：所有图形都在 24×24 的坐标系里写，内容落在 3–21 的活动区内，再按实际尺寸整体缩放，
/// 这样图标在不同 dpi 下都保持同一套比例。
///
/// 四条统一规则（上一版没有规则，所以看着糙）：
///  1. 一律描边，不用填充。选中状态由标签栏的胶囊底色表达，图标不再自己加重。
///  2. 线宽只有一种（1.7），线端与拐角一律圆头，不做粗细变化。
///  3. 每个图标最多两个几何概念：气泡 / 四格 / 勾+线 / 齿轮+圆环。
///  4. 圆角与曲线全部用三次贝塞尔近似（Kappa），不再用 AddArc —— 上一版把宽高当成
///     第二组坐标传给了 AddArc，四个圆角其实是退化的椭圆弧，这正是"糊"的来源之一。
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

        /// <summary>语音输入：胶囊麦克风 + 下方的弧形底座。</summary>
        Microphone,

        /// <summary>发送：向上的箭头。</summary>
        Send,

        /// <summary>导出：向下的箭头 + 落点横线。</summary>
        Download,
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

    /// <summary>线宽（24×24 坐标系里）。全套图标只有这一个线宽。</summary>
    private const float Stroke = 1.7f;

    /// <summary>90° 圆弧的三次贝塞尔近似系数。</summary>
    private const float Kappa = 0.5523f;

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
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.StrokeLineCap = LineCap.Round;
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
            case IconKind.Microphone:
                DrawMicrophone(canvas);
                break;
            case IconKind.Send:
                DrawSend(canvas);
                break;
            case IconKind.Download:
                DrawDownload(canvas);
                break;
        }

        canvas.RestoreState();
    }

    /// <summary>对话：一个带小尾巴的圆角气泡。</summary>
    private static void DrawChat(ICanvas canvas)
    {
        const float radius = 4.5f;

        var bubble = new PathF();
        bubble.MoveTo(7.5f, 4f);
        bubble.LineTo(16.5f, 4f);
        bubble.CurveTo(16.5f + Kappa * radius, 4f, 21f, 8.5f - Kappa * radius, 21f, 8.5f);
        bubble.LineTo(21f, 12.5f);
        bubble.CurveTo(21f, 12.5f + Kappa * radius, 16.5f + Kappa * radius, 17f, 16.5f, 17f);
        bubble.LineTo(13.4f, 17f);
        bubble.LineTo(9.4f, 21.1f);
        bubble.LineTo(8f, 17f);
        bubble.LineTo(7.5f, 17f);
        bubble.CurveTo(7.5f - Kappa * radius, 17f, 3f, 12.5f + Kappa * radius, 3f, 12.5f);
        bubble.LineTo(3f, 8.5f);
        bubble.CurveTo(3f, 8.5f - Kappa * radius, 7.5f - Kappa * radius, 4f, 7.5f, 4f);
        bubble.Close();

        canvas.DrawPath(bubble);
    }

    /// <summary>看板：2×2 等分的圆角方块。四格同尺寸同圆角，靠统一而不是靠某个格子加重来表达"总览"。</summary>
    private static void DrawDashboard(ICanvas canvas)
    {
        const float side = 8f;
        const float radius = 2.5f;

        foreach (var (x, y) in new[] { (3f, 3f), (13f, 3f), (3f, 13f), (13f, 13f) })
        {
            canvas.DrawPath(RoundedRect(x, y, side, side, radius));
        }
    }

    /// <summary>任务：三行等距的「勾 + 文本线」。勾的形状与线宽完全一致，不再混用实心圆点。</summary>
    private static void DrawTasks(ICanvas canvas)
    {
        foreach (var y in new[] { 6.5f, 12f, 17.5f })
        {
            var tick = new PathF();
            tick.MoveTo(3.3f, y);
            tick.LineTo(5.1f, y + 1.8f);
            tick.LineTo(8.3f, y - 2f);
            canvas.DrawPath(tick);

            canvas.DrawLine(11.5f, y, 20.8f, y);
        }
    }

    /// <summary>
    /// 设置：六齿齿轮轮廓 + 中心圆环。
    ///
    /// 齿用多边形画，不用圆弧拼接：上一版每个齿都靠 AddArc(q1, q2, 0, 90) 从内侧点弧到内侧点，
    /// 参数本身没有意义，画出来是一圈锯齿。六齿（而不是八齿）是为了在 22dpi 的实际尺寸下
    /// 每个齿还有 3.7 个单位的宽度，不会糊成一片。
    /// </summary>
    private static void DrawSettings(ICanvas canvas)
    {
        const float cx = 12f, cy = 12f;
        const float tip = 8.7f, root = 6.8f;
        const float tipHalf = 11f, rootHalf = 18f;

        var gear = new PathF();
        for (var i = 0; i < 6; i++)
        {
            var center = i * 60f;

            var start = Polar(cx, cy, root, center - rootHalf);
            var up = Polar(cx, cy, tip, center - tipHalf);
            var down = Polar(cx, cy, tip, center + tipHalf);
            var end = Polar(cx, cy, root, center + rootHalf);

            if (i == 0) gear.MoveTo(start);
            else gear.LineTo(start);

            gear.LineTo(up);
            gear.LineTo(down);
            gear.LineTo(end);
        }

        gear.Close();
        canvas.DrawPath(gear);
        canvas.DrawCircle(cx, cy, 3f);
    }

    /// <summary>语音输入：竖直胶囊 + 下方弧形底座，两笔，无实心。</summary>
    private static void DrawMicrophone(ICanvas canvas)
    {
        canvas.DrawPath(RoundedRect(9.3f, 3f, 5.4f, 9.6f, 2.7f));

        const float cx = 12f, cy = 11.6f, radius = 7.4f;
        var cradle = new PathF();
        cradle.MoveTo(cx - radius, cy);
        cradle.CurveTo(cx - radius, cy + Kappa * radius, cx - Kappa * radius, cy + radius, cx, cy + radius);
        cradle.CurveTo(cx + Kappa * radius, cy + radius, cx + radius, cy + Kappa * radius, cx + radius, cy);
        canvas.DrawPath(cradle);
    }

    /// <summary>发送：竖直箭杆 + 两笔箭头，交汇于同一点。</summary>
    private static void DrawSend(ICanvas canvas)
    {
        const float apex = 4.2f;

        canvas.DrawLine(12f, 20.6f, 12f, apex);

        var head = new PathF();
        head.MoveTo(5.8f, 10.8f);
        head.LineTo(12f, apex);
        head.LineTo(18.2f, 10.8f);
        canvas.DrawPath(head);
    }

    /// <summary>导出：向下的箭头 + 一条落点横线，和"发送"的向上箭头区分开。</summary>
    private static void DrawDownload(ICanvas canvas)
    {
        const float tip = 16.2f;

        canvas.DrawLine(12f, 3.2f, 12f, tip);

        var head = new PathF();
        head.MoveTo(5.8f, 10f);
        head.LineTo(12f, tip);
        head.LineTo(18.2f, 10f);
        canvas.DrawPath(head);

        canvas.DrawLine(4.6f, 20.4f, 19.4f, 20.4f);
    }

    private static PointF Polar(float cx, float cy, float radius, float degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new PointF(
            cx + (float)(Math.Cos(radians) * radius),
            cy + (float)(Math.Sin(radians) * radius));
    }

    /// <summary>圆角矩形，四个角用三次贝塞尔近似 90° 圆弧。</summary>
    private static PathF RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new PathF();
        path.MoveTo(x + radius, y);
        path.LineTo(x + width - radius, y);
        path.CurveTo(x + width - radius + Kappa * radius, y, x + width, y + radius - Kappa * radius, x + width, y + radius);
        path.LineTo(x + width, y + height - radius);
        path.CurveTo(x + width, y + height - radius + Kappa * radius,
            x + width - radius + Kappa * radius, y + height, x + width - radius, y + height);
        path.LineTo(x + radius, y + height);
        path.CurveTo(x + radius - Kappa * radius, y + height, x, y + height - radius + Kappa * radius, x, y + height - radius);
        path.LineTo(x, y + radius);
        path.CurveTo(x, y + radius - Kappa * radius, x + radius - Kappa * radius, y, x + radius, y);
        path.Close();
        return path;
    }
}
