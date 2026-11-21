using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace FairyAI_Android.Services.Theme;

/// <summary>
/// 玻璃层原语。
///
/// 为什么不是「整块真模糊」：
///   Android 上真正的背景模糊要走 RenderEffect（API 31+），而 MAUI 的 BlurEffect 只模糊
///   **控件自己的内容**，不采样背后的东西 —— 也就是说它能让卡片内部变柔，却不会让
///   卡片下面的页面透出来。
///
///   液态玻璃真正的视觉特征有三个，缺一不可：
///     1. 半透明 tint（能看见底色）
///     2. 上缘高光 + 一圈细边（勾出厚度）
///     3. 底缘内阴影（让人感觉玻璃浮起来）
///   这三样用 Brush + 边框 + 阴影就能做出来，而且各主题可调；真模糊只是锦上添花，
///   在 API 24 的机器上还会掉帧。所以这里做成「可降级」：能模糊就模糊，不能就退回 tint。
///
/// 关键约束：MAUI 的 Border 只支持单层背景。要同时有「斜向高光」和「半透明 tint」，
/// 做法是叠两层 Border —— 外层放斜向渐变，内层放纯 tint。
/// </summary>
public static class Glass
{
    /// <summary>
    /// 玻璃容器的统一外观：一个 Border 套一个 Border。
    /// 外层画高光渐变与描边，内层画 tint 与圆角，两者共享同一套圆角数值。
    /// </summary>
    public static Border Container(
        Color tint,
        Color border,
        Color highlight,
        double cornerRadius = 16,
        double borderThickness = 1,
        Color? sheen = null)
    {
        var outer = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(cornerRadius) },
            StrokeThickness = borderThickness,
            Stroke = new SolidColorBrush(border),
            Background = BuildSheen(highlight, sheen),
            Padding = 0,
        };

        var inner = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(cornerRadius) },
            StrokeThickness = 0,
            Background = new SolidColorBrush(tint),
            Padding = 0,
        };

        outer.Content = inner;
        return outer;
    }

    /// <summary>
    /// 顶部斜向高光。玻璃看起来有厚度，靠的就是这一条从左上到右下的亮边。
    /// 末端再叠一个更弱的白色，模拟环境光的二次反射。
    /// </summary>
    private static Brush BuildSheen(Color highlight, Color? sheen)
    {
        var transparent = new Color(highlight.Red, highlight.Green, highlight.Blue, (byte)0);

        var stops = new GradientStopCollection
        {
            new GradientStop(highlight, 0.0f),
            new GradientStop(transparent, 0.30f),
            new GradientStop(transparent, 0.72f),
        };

        if (sheen is not null)
        {
            stops.Add(new GradientStop(
                new Color(sheen.Red, sheen.Green, sheen.Blue, (byte)(sheen.Alpha * 0.35f)), 1.0f));
        }

        return new LinearGradientBrush(stops)
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
    }

    /// <summary>
    /// 玻璃容器在主题里要用的那一套色。主题切换时值会变，所以每次现取而不是缓存。
    /// </summary>
    public static (Color Tint, Color TintStrong, Color Border, Color Highlight, Color Shadow, Color Sheen) Palette()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            // 应用还没起来（或者在后台线程取色）：退回墨黑的值，不返回透明黑。
            return (new Color(0xFF, 0xFF, 0xFF, 0x14),
                    new Color(0xFF, 0xFF, 0xFF, 0x22),
                    new Color(0xFF, 0xFF, 0xFF, 0x26),
                    new Color(0xFF, 0xFF, 0xFF, 0x3D),
                    new Color(0x00, 0x00, 0x00, 0x40),
                    new Color(0xFF, 0xFF, 0xFF, 0x1A));
        }

        return (
            ReadColor(resources, "Theme.GlassTint"),
            ReadColor(resources, "Theme.GlassTintStrong"),
            ReadColor(resources, "Theme.GlassBorder"),
            ReadColor(resources, "Theme.GlassHighlight"),
            ReadColor(resources, "Theme.GlassShadow"),
            ReadColor(resources, "Theme.GlassSheen"));
    }

    /// <summary>
    /// 从应用资源里取主题颜色。
    /// 主题切换后同一个页面拿到的就是新主题的颜色。
    /// </summary>
    public static Color ReadColor(ResourceDictionary resources, string key)
    {
        if (resources.TryGetValue(key, out var value) && value is Color color) return color;
        return Colors.Transparent;
    }

    /// <summary>取主题里的普通颜色（文字、强调色等）。</summary>
    public static Color ReadColor(string key)
    {
        var resources = Application.Current?.Resources;
        return resources is null ? Colors.Transparent : ReadColor(resources, key);
    }

    /// <summary>
    /// 给玻璃容器加浮起感：外阴影。
    /// MAUI 的 Shadow 只有外阴影；内阴影要靠一层反转渐变自己画，见 BottomInnerShadow。
    /// </summary>
    public static void ApplyDepth(Border container, Color shadow, double radius = 18, double opacity = 0.35)
    {
        container.Shadow = new Shadow
        {
            Brush = new SolidColorBrush(shadow),
            Radius = (float)radius,
            Opacity = (float)opacity,
            Offset = new Point(0, 6),
        };
    }

    /// <summary>
    /// 底缘内阴影层。返回一个盖在容器内容之下的 Border，用来给玻璃「厚度」。
    /// </summary>
    public static Border BottomInnerShadow(Color color, double cornerRadius = 16)
    {
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(cornerRadius) },
            StrokeThickness = 0,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(new Color(color.Red, color.Green, color.Blue, (byte)0), 0.0f),
                new GradientStop(color, 1.0f),
            })
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            },
        };
    }
}
