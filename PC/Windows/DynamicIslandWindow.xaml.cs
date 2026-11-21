using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyAiAssistant.Services.Theme;

namespace MyAiAssistant.Windows;

public partial class DynamicIslandWindow : Window
{
    public event Action? CloseClicked;
    public event Action? IslandActivated;
    private readonly Rectangle[] _waveBars = new Rectangle[20];
    private bool _isVisible;

    public DynamicIslandWindow()
    {
        InitializeComponent();
        InitWaveformBars();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 4;
    }

    private void InitWaveformBars()
    {
        // 灵动岛的波形和 WaveformVisualizer 是同一个视觉元素，但各画各的
        // （一个在控件里、一个在窗口里），所以颜色也得取同一套主题色。
        var accent = ThemeManager.GetColor("Theme.Accent", Colors.DeepSkyBlue);
        var success = ThemeManager.GetColor("Theme.Success", Colors.MediumSpringGreen);
        var accentSoft = ThemeManager.GetColor("Theme.AccentSoft", Colors.SteelBlue);

        var colors = new Brush[]
        {
            new LinearGradientBrush(accent, accentSoft, 90),
            new LinearGradientBrush(success, accent, 90),
            new LinearGradientBrush(accentSoft, accent, 90),
        };

        for (int i = 0; i < _waveBars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 2,
                Height = 4,
                Margin = new Thickness(1, 0, 1, 0),
                RadiusX = 1,
                RadiusY = 1,
                Fill = colors[i % colors.Length]
            };
            _waveBars[i] = bar;
            WaveformPanel.Children.Add(bar);
        }
    }

    public void ShowIsland()
    {
        if (_isVisible) return;
        _isVisible = true;

        Show();
        Activate();

        IslandScale.ScaleX = 0.1;
        IslandScale.ScaleY = 0.05;
        IslandTranslate.Y = -20;

        var scaleX = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation(0.05, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var translate = new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        IslandTranslate.BeginAnimation(TranslateTransform.YProperty, translate);

        IslandActivated?.Invoke();
    }

    public void HideIsland()
    {
        if (!_isVisible) return;
        _isVisible = false;

        var scaleX = new DoubleAnimation(1.0, 0.1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var scaleY = new DoubleAnimation(1.0, 0.05, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var translate = new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        scaleY.Completed += (_, _) => Hide();

        IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        IslandTranslate.BeginAnimation(TranslateTransform.YProperty, translate);
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    public void ShowWaveform()
    {
        WaveformPanel.Visibility = Visibility.Visible;
        ResponseScroll.Visibility = Visibility.Collapsed;
        TranscriptionText.Visibility = Visibility.Collapsed;
    }

    public void ShowTranscription(string text)
    {
        WaveformPanel.Visibility = Visibility.Collapsed;
        TranscriptionText.Text = text;
        TranscriptionText.Visibility = Visibility.Visible;
        ResponseScroll.Visibility = Visibility.Collapsed;
    }

    public void ShowResponse(string text)
    {
        WaveformPanel.Visibility = Visibility.Collapsed;
        TranscriptionText.Visibility = Visibility.Collapsed;
        ResponseScroll.Visibility = Visibility.Visible;
        ResponseViewer.Markdown = text;
        ResponseViewer.Pipeline = Services.MarkdownService.Pipeline;
        ResponseScroll.ScrollToTop();
    }

    public void UpdateWaveform(float amplitude)
    {
        var maxH = 24.0;
        for (int i = 0; i < _waveBars.Length; i++)
        {
            double wave = Math.Sin((Environment.TickCount * 0.005) + i * 0.4);
            double h = 4 + (wave * 0.5 + 0.5) * maxH * Math.Max(amplitude, 0.1);
            _waveBars[i].Height = Math.Max(4, h);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        CloseClicked?.Invoke();
    }
}
