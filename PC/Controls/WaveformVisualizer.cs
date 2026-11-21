using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyAiAssistant.Services.Theme;

namespace MyAiAssistant.Controls;

public class WaveformVisualizer : ContentControl
{
    private const int BarCount = 24;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _targetHeights = new double[BarCount];
    private readonly double[] _currentHeights = new double[BarCount];
    private readonly DispatcherTimer _timer;
    private double _amplitude;

    /// <summary>
    /// The three bar gradients follow the theme: the accent, the success colour and a
    /// softer variant of the accent. They are built lazily because the theme can change
    /// while the app is running, and a gradient made at construction time would keep
    /// the old colours.
    /// </summary>
    private static Brush[] BuildBarBrushes()
    {
        var accent = ThemeManager.GetColor("Theme.Accent", Colors.DeepSkyBlue);
        var success = ThemeManager.GetColor("Theme.Success", Colors.MediumSpringGreen);
        var accentSoft = ThemeManager.GetColor("Theme.AccentSoft", Colors.SteelBlue);

        return
        [
            MakeGradient(accent, accentSoft),
            MakeGradient(success, accent),
            MakeGradient(accentSoft, accent),
        ];
    }

    private static Brush MakeGradient(Color from, Color to) =>
        new LinearGradientBrush(from, to, 90);

    private static Brush[]? _barBrushes;

    private static Brush[] BarBrushes => _barBrushes ??= BuildBarBrushes();

    /// <summary>Called when the theme changes so the bars pick up the new colours.</summary>
    public static void RefreshTheme() => _barBrushes = BuildBarBrushes();

    public double Amplitude
    {
        get => (double)GetValue(AmplitudeProperty);
        set => SetValue(AmplitudeProperty, value);
    }

    public static readonly DependencyProperty AmplitudeProperty =
        DependencyProperty.Register(nameof(Amplitude), typeof(double), typeof(WaveformVisualizer),
            new PropertyMetadata(0.0, (d, e) =>
            {
                if (d is WaveformVisualizer w) w._amplitude = Math.Clamp((double)e.NewValue, 0, 1);
            }));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(WaveformVisualizer),
            new PropertyMetadata(false));

    public WaveformVisualizer()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop() { _timer.Stop(); for (int i = 0; i < BarCount; i++) _targetHeights[i] = 2; }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 2, Height = 4, Margin = new Thickness(1, 0, 1, 0),
                RadiusX = 1, RadiusY = 1,
                Fill = BarBrushes[i % BarBrushes.Length]
            };
            _bars[i] = bar;
            _currentHeights[i] = 4;
            panel.Children.Add(bar);
        }

        Content = panel;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsActiveProperty)
        {
            if (IsActive) _timer.Start();
            else _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double maxH = 28;

        if (IsActive)
        {
            for (int i = 0; i < BarCount; i++)
            {
                double wave = Math.Sin((Environment.TickCount * 0.005) + i * 0.4);
                _targetHeights[i] = Math.Clamp(4 + (wave * 0.5 + 0.5) * maxH * Math.Max(_amplitude, 0.15), 2, maxH);
            }
        }
        else
        {
            for (int i = 0; i < BarCount; i++) _targetHeights[i] = 2;
        }

        for (int i = 0; i < BarCount; i++)
        {
            _currentHeights[i] += (_targetHeights[i] - _currentHeights[i]) * 0.3;
            _bars[i].Height = Math.Max(2, _currentHeights[i]);
        }
    }
}
