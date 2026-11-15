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
/// </summary>
public partial class DesktopDockWindow : Window
{
    /// <summary>Width while collapsed. Small enough to sit outside a title bar's close button.</summary>
    public const double CollapsedWidth = 6;

    /// <summary>Width while the pointer is over the rail.</summary>
    public const double ExpandedWidth = 40;

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
                var path = new Path
                {
                    Data = icon.Vector,
                    Stroke = IconBrush,
                    StrokeThickness = 1.4,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stretch = Stretch.Uniform,
                    Width = 17,
                    Height = 17,
                    SnapsToDevicePixels = true
                };
                artwork = path;
                tint = brush => path.Stroke = brush;
            }
            else
            {
                // A bitmap keeps its own pixels; tinting an opacity mask instead of the image
                // lets one monochrome PNG serve the normal, hover and disabled states.
                var mask = new ImageBrush(icon.Bitmap) { Stretch = Stretch.Uniform };
                var shape = new Rectangle
                {
                    Width = 17,
                    Height = 17,
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
                Width = 32,
                Height = 32,
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