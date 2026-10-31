using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InkFlow.Services;

namespace InkFlow.Views;

public enum ToastKind
{
    Info,
    Success,
    Rest,
    Warning,
}

/// <summary>A small self-dismissing notification in the corner of the screen.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private double? _aboveTop;

    public ToastWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4.6),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HideAnimated();
        };

        SourceInitialized += (_, _) => WindowEffects.MakeOverlay(this);
        SizeChanged += (_, _) => Reposition();
        MouseLeftButtonDown += (_, _) => HideAnimated();
    }

    public void ShowToast(string title, string message, ToastKind kind, double? aboveTop = null)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        _aboveTop = aboveTop;

        AccentBar.Background = (Brush)FindResource(kind switch
        {
            ToastKind.Success => "Accent2Brush",
            ToastKind.Rest => "GoldBrush",
            ToastKind.Warning => "DangerBrush",
            _ => "AccentBrush",
        });

        _hideTimer.Stop();
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        Reposition();

        BeginAnimation(OpacityProperty, null);
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        _hideTimer.Start();
    }

    private void Reposition()
    {
        if (!IsVisible || ActualWidth <= 0)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 10;
        var top = area.Bottom - ActualHeight - 10;
        if (_aboveTop is { } above)
        {
            top = Math.Min(top, above - ActualHeight - 10);
        }

        Top = Math.Max(area.Top + 8, top);
    }

    private void HideAnimated()
    {
        if (!IsVisible)
        {
            return;
        }

        _hideTimer.Stop();
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(170));
        animation.Completed += (_, _) =>
        {
            Hide();
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, animation);
    }
}
