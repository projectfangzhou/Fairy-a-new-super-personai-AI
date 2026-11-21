using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using FairyAI_Android.Services;
using FairyAI_Android.Services.Theme;

namespace FairyAI_Android.Controls;

/// <summary>
/// 悬浮玻璃标签栏。
///
/// 为什么不用 Shell 自带的 TabBar：
///   Shell 的 TabBar 只能设背景色，做不出「胶囊气泡浮在内容之上」的效果，
///   而且图标在安卓上默认不显示（TabBar 高度也不够放气泡）。
///   所以自己画一个绝对定位的浮层挂在 Shell 上面。
///
/// 气泡做法：每个标签是一个圆角 Border，选中态用强调色填充 + 发光，
/// 未选中态是半透明玻璃面。靠 TapGestureRecognizer 切换路由。
/// </summary>
public sealed class GlassTabBar : Grid
{
    private readonly List<GlassTabItem> _items = new();
    private readonly Border _bar;
    private readonly HorizontalStackLayout _row;

    /// <summary>标签定义。</summary>
    private sealed record GlassTabItem(string Title, string Icon, string Route);

    public GlassTabBar()
    {
        // 外层透明，只负责定位；真正的玻璃在 _bar 上。
        RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) };
        ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star) };
        Padding = new Thickness(0);
        VerticalOptions = LayoutOptions.End;
        HorizontalOptions = LayoutOptions.Center;
        Margin = new Thickness(0, 0, 0, 18);

        _row = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
        };

        _bar = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28) },
            StrokeThickness = 1,
            Padding = new Thickness(6),
            Background = BuildBarBackground(),
        };

        var stroke = new SolidColorBrush(Glass.ReadColor("Theme.GlassBorder"));
        _bar.Stroke = stroke;

        var shadow = new Shadow
        {
            Brush = new SolidColorBrush(Glass.ReadColor("Theme.GlassShadow")),
            Radius = 20,
            Opacity = 0.5f,
            Offset = new Point(0, 8),
        };
        _bar.Shadow = shadow;

        _bar.Content = _row;
        Add(_bar);

        SetItems(DefaultItems);
    }

    /// <summary>
    /// 把一个标签栏挂到页面底部。
    ///
    /// 页面原本的内容整体挪到上面一行，气泡栏占下面一行——占真实布局空间而不是浮在上面，
    /// 否则会盖住对话页的输入栏和列表最后一项。四个主标签页各调一次。
    /// </summary>
    public static GlassTabBar AttachTo(ContentPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var bar = new GlassTabBar();
        var original = page.Content;

        // Shell 的这条附加属性只有设在页面本身上才生效；设在 Shell 根节点或 Tab 上都不管用，
        // 安卓上内置那条标签栏照样会画出来，跟气泡栏重复。
        Shell.SetTabBarIsVisible(page, false);

        var host = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };

        if (original is not null)
        {
            host.Add(original);
            Grid.SetRow(original, 0);
        }

        host.Add(bar);
        Grid.SetRow(bar, 1);
        page.Content = host;
        return bar;
    }

    /// <summary>四个主标签。写死在这里，因为页面里的实例不该各自维护一份路由表。</summary>
    private static readonly (string Title, string Icon, string Route)[] DefaultItems =
    {
        ("对话", "chat", "chat"),
        ("看板", "dashboard", "dashboard"),
        ("任务", "tasks", "tasks"),
        ("设置", "settings", "settings"),
    };

    /// <summary>
    /// 每页各挂一个实例，选中态要跟着 Shell 的导航走，所以在这里订阅。
    /// 订阅前先退订，避免同一个实例被恢复视图时重复挂事件。
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (HostShell is { } shell)
        {
            shell.Navigated -= OnShellNavigated;
            if (Handler is not null) shell.Navigated += OnShellNavigated;
        }

        // 这个控件是 C# 画的，颜色在构造时就取成了具体值，没有 DynamicResource 帮忙刷新，
        // 所以换主题必须自己重画一遍。挂在 handler 上是为了页面销毁时能干净退订。
        ThemeManager.ThemeChanged -= OnThemeChanged;
        if (Handler is not null) ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshSelection();
    }

    /// <summary>
    /// 所属的 Shell。优先从父链往上找，找不到才退到 <see cref="Shell.Current"/>。
    ///
    /// 换主题时整个 Shell 会被重建（window.Page = new AppShell()），而 Shell.Current 未必
    /// 立刻指向新的那个——直接用它会出现「气泡栏点了没反应」。沿父链找拿到的永远是真正承载
    /// 本控件的那一个。
    /// </summary>
    private Shell? HostShell
    {
        get
        {
            Element? element = this;
            while (element is not null)
            {
                if (element is Shell shell) return shell;
                element = element.Parent;
            }

            return Shell.Current;
        }
    }

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e) => RefreshSelection();

    private void OnThemeChanged(FairyTheme theme) => RefreshTheme();

    /// <summary>按当前主题重画玻璃底、描边、发光与各标签的选中态。</summary>
    private void RefreshTheme()
    {
        _bar.Background = BuildBarBackground();
        _bar.Stroke = new SolidColorBrush(Glass.ReadColor("Theme.GlassBorder"));
        _bar.Shadow = new Shadow
        {
            Brush = new SolidColorBrush(Glass.ReadColor("Theme.GlassShadow")),
            Radius = 20,
            Opacity = 0.5f,
            Offset = new Point(0, 8),
        };

        RefreshSelection();
    }

    /// <summary>标签定义。调用一次即可，路由用 Shell 的绝对路由。</summary>
    public void SetItems(IEnumerable<(string Title, string Icon, string Route)> items)
    {
        _items.Clear();
        _row.Children.Clear();

        foreach (var (title, icon, route) in items)
        {
            _items.Add(new GlassTabItem(title, icon, route));
            _row.Children.Add(BuildTab(title, icon, route));
        }

        RefreshSelection();
    }

    /// <summary>更新当前选中项。Shell 切页时调用。</summary>
    public void RefreshSelection()
    {
        var current = CurrentRoute();
        for (var i = 0; i < _row.Children.Count; i++)
        {
            if (_row.Children[i] is not Border border) continue;
            var selected = _items.Count > i && _items[i].Route == current;
            ApplyTabState(border, selected);
        }
    }

    private string CurrentRoute()
    {
        var location = HostShell?.CurrentState?.Location?.OriginalString ?? string.Empty;
        // Shell 的 CurrentState 给的是 "//chat" / "chat" / "//dashboard" 这种形式。
        var trimmed = location.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash >= 0) trimmed = trimmed[..slash];
        return trimmed.ToLowerInvariant();
    }

    private Border BuildTab(string title, string icon, string route)
    {
        var iconView = new FairyIcon
        {
            Kind = icon switch
            {
                "dashboard" => FairyIcon.IconKind.Dashboard,
                "tasks" => FairyIcon.IconKind.Tasks,
                "settings" => FairyIcon.IconKind.Settings,
                _ => FairyIcon.IconKind.Chat,
            },
            WidthRequest = 22,
            HeightRequest = 22,
            VerticalOptions = LayoutOptions.Center,
        };

        var label = new Label
        {
            Text = title,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };

        var content = new HorizontalStackLayout
        {
            Spacing = 7,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Padding = new Thickness(12, 9),
        };
        content.Add(iconView);
        content.Add(label);

        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20) },
            StrokeThickness = 0,
            Content = content,
            Padding = 0,
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await GoToAsync(route);
        border.GestureRecognizers.Add(tap);

        ApplyTabState(border, false);
        return border;
    }

    /// <summary>
    /// 选中态：强调色填充 + 发光阴影。未选中态：半透明玻璃面。
    /// 图标和文字都跟着换色，所以两套颜色都要给。
    /// </summary>
    private static void ApplyTabState(Border border, bool selected)
    {
        var accent = Glass.ReadColor("Theme.Accent");
        var onAccent = Glass.ReadColor("Theme.TextOnAccent");
        var textPrimary = Glass.ReadColor("Theme.TextPrimary");
        var tint = Glass.ReadColor("Theme.GlassTintStrong");

        if (selected)
        {
            border.Background = new SolidColorBrush(accent);
            border.Stroke = new SolidColorBrush(accent);
            border.Shadow = new Shadow
            {
                Brush = new SolidColorBrush(accent),
                Radius = 14,
                Opacity = 0.45f,
                Offset = new Point(0, 4),
            };
        }
        else
        {
            border.Background = new SolidColorBrush(tint);
            border.Stroke = Colors.Transparent;
            border.Shadow = null;
        }

        if (border.Content is not HorizontalStackLayout content) return;
        foreach (var child in content.Children)
        {
            switch (child)
            {
                case FairyIcon icon:
                    icon.Tint = selected ? onAccent : textPrimary;
                    break;
                case Label label:
                    label.TextColor = selected ? onAccent : textPrimary;
                    break;
            }
        }
    }

    private static Brush BuildBarBackground()
    {
        var tint = Glass.ReadColor("Theme.GlassTint");
        var tintStrong = Glass.ReadColor("Theme.GlassTintStrong");
        return new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(tint, 0.0f),
            new GradientStop(tintStrong, 1.0f),
        })
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
    }

    private async Task GoToAsync(string route)
    {
        if (HostShell is not { } shell) return;
        await shell.GoToAsync("//" + route);
    }
}
