using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyAiAssistant.ViewModels;

namespace MyAiAssistant.Windows;

/// <summary>
/// A slim rail pinned to the right edge of the work area.
///
/// It rests as a few pixels of edge so it cannot swallow a window's close button, and slides
/// out when the pointer reaches it. Icons are stroked vector paths loaded from Assets/Dock so
/// artwork can be replaced without a build.
///
/// 背景是纯黑：它常驻在屏幕右缘，深灰在很多壁纸和窗口边框上会显出一道缝，纯黑不会。
/// </summary>
public partial class DesktopDockWindow : Window
{
    /// <summary>Width while collapsed. Small enough to sit outside a title bar's close button.</summary>
    public const double CollapsedWidth = 4;

    /// <summary>Width while the pointer is over the rail.</summary>
    public const double ExpandedWidth = 34;

    private static readonly SolidColorBrush IconBrush = new(System.Windows.Media.Color.FromRgb(0xC9, 0xC7, 0xD8));
    private static readonly SolidColorBrush AccentBrush = new(System.Windows.Media.Color.FromRgb(0x7B, 0x68, 0xEE));
    private static readonly SolidColorBrush HoverSurface = new(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x3E));

    private readonly Action<DockAction> _onActivate;
    private readonly List<(DockEntry Entry, Button Button, FrameworkElement Icon)> _items = [];
    private readonly DispatcherTimer _collapseTimer;
    private bool _expanded;

    public DesktopDockWindow(Action<DockAction> onActivate)
    {
        _onActivate = onActivate ?? throw new ArgumentNullException(nameof(onActivate));
        InitializeComponent();
        // The rail must never take focus away from what the user is doing.
        ShowActivated = false;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver) Collapse();
        };

        BuildRail();
        DockHost.Visibility = Visibility.Collapsed;
        BottomHost.Visibility = Visibility.Collapsed;
        ApplyGeometry();
        Loaded += (_, _) => ApplyGeometry();

        MouseEnter += (_, _) => Expand();
        MouseLeave += (_, _) => _collapseTimer.Start();
    }

    public bool IsExpanded => _expanded;

    private void BuildRail()
    {
        foreach (var entry in DockCatalog.Entries)
        {
            var icon = DockIconLoader.Load(entry.IconKey, entry.IconData);
            FrameworkElement artwork;
            Action<Brush> tint;

            if (icon.Vector is not null)
            {
                // 「描边 + 实心」的混合图标：主线用描边，强调部分（点、滑块、指针轴心）
                // 单独一层用填充。WPF 的 Path 没法对同一个 Geometry 同时描边一部分、填充另一部分，
                // 所以拆成两层叠在一起。
                var group = new Grid { Width = 18, Height = 18 };

                var line = new Path
                {
                    Data = icon.Vector,
                    Stroke = IconBrush,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
                group.Children.Add(line);

                Path? accent = null;
                if (!string.IsNullOrWhiteSpace(entry.AccentData))
                {
                    try
                    {
                        accent = new Path
                        {
                            Data = Geometry.Parse(entry.AccentData),
                            Fill = IconBrush,
                            Stroke = System.Windows.Media.Brushes.Transparent,
                            Stretch = Stretch.Uniform,
                            SnapsToDevicePixels = true
                        };
                        group.Children.Add(accent);
                    }
                    catch (FormatException)
                    {
                        // 图标数据坏了就退回纯描边，不让整条栏出不来。
                    }
                }

                artwork = group;
                tint = brush =>
                {
                    line.Stroke = brush;
                    if (accent is not null) accent.Fill = brush;
                };
            }
            else
            {
                // A bitmap keeps its own pixels; tinting an opacity mask instead of the image
                // lets one monochrome PNG serve the normal, hover and disabled states.
                var mask = new ImageBrush(icon.Bitmap) { Stretch = Stretch.Uniform };
                var shape = new Rectangle
                {
                    Width = 18,
                    Height = 18,
                    Fill = IconBrush,
                    OpacityMask = mask,
                    Stretch = Stretch.Uniform
                };
                artwork = shape;
                tint = brush => shape.Fill = brush;
            }

            var button = new Button
            {
                Content = artwork,
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                IsEnabled = entry.IsEnabled,
                ToolTip = entry.IsEnabled ? entry.Title : $"{entry.Title} · {entry.UnavailableReason}"
            };

            if (!entry.IsEnabled)
            {
                button.Opacity = 0.32;
            }
            else
            {
                button.Cursor = Cursors.Hand;
                button.MouseEnter += (_, _) =>
                {
                    button.Background = HoverSurface;
                    tint(AccentBrush);
                };
                button.MouseLeave += (_, _) =>
                {
                    button.Background = System.Windows.Media.Brushes.Transparent;
                    tint(IconBrush);
                };
                var action = entry.Action;
                button.Click += (_, _) => _onActivate(action);
            }

            _items.Add((entry, button, artwork));

            // Settings belongs at the foot of the rail, everything else stacks from the top.
            var host = entry.Action == DockAction.Settings ? BottomHost : DockHost;
            host.Children.Add(button);
        }
    }

    public void Expand()
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expanded = true;
        DockHost.Visibility = Visibility.Visible;
        BottomHost.Visibility = Visibility.Visible;
        ApplyGeometry();
    }

    public void Collapse()
    {
        if (!_expanded && Width.Equals(CollapsedWidth)) return;
        _expanded = false;
        DockHost.Visibility = Visibility.Collapsed;
        BottomHost.Visibility = Visibility.Collapsed;
        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        var area = SystemParameters.WorkArea;
        Width = _expanded ? ExpandedWidth : CollapsedWidth;
        Height = area.Height;
        Left = area.Right - Width;
        Top = area.Top;
    }

    internal IReadOnlyList<(DockEntry Entry, Button Button, FrameworkElement Icon)> Items => _items;
}