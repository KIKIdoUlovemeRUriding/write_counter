using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using InkFlow.Models;
using InkFlow.Services;
using WinForms = System.Windows.Forms;

namespace InkFlow.Views;

public partial class OverlayWindow : Window
{
    private readonly AppState _state;
    private readonly DispatcherTimer _positionTimer;
    private readonly System.Windows.Media.Effects.Effect? _cardShadow;

    private bool _loaded;
    private bool _restoring;
    private bool _dragging;
    private bool _adjustingLayout;
    private bool _titleEditorOpen;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

    public OverlayWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        DataContext = state;
        _cardShadow = ShadowHost.Effect;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _positionTimer.Tick += (_, _) =>
        {
            _positionTimer.Stop();
            SavePosition();
        };

        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeOverlay(this);
        };
        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        LostMouseCapture += (_, _) => _dragging = false;
        _state.Settings.PropertyChanged += Settings_PropertyChanged;
        _state.Engine.Tick += (_, _) => UpdateToggleGlyphs();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        ApplyScale(updateLayout: false);
        ApplyExpanded(preserveBottom: false);
        ApplyClickThrough();
        ApplyMinimizedLayout(_state.Settings.OverlayMinimized);
        RestorePosition();
        UpdateToggleGlyphs();
        _loaded = true;
        Opacity = 1;
        SavePosition();
    }

    public void EnsureVisible()
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        ClampToScreen();
        _loaded = true;
        SavePosition();
    }

    // ---------------------------------------------------------------- layout

    private void ApplyScale(bool updateLayout = true)
    {
        var scale = _state.Settings.OverlayScale;
        ScaleRoot.ScaleX = scale;
        ScaleRoot.ScaleY = scale;
        if (updateLayout)
        {
            UpdateLayout();
            ClampToScreen();
        }
    }

    private void ApplyExpanded(bool preserveBottom)
    {
        var expanded = _state.Settings.OverlayExpanded;
        var bottom = Top + ActualHeight;

        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CompactProgress.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        MaximizeButton.Content = expanded ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = expanded ? "还原为紧凑模式" : "展开详情";

        UpdateLayout();
        if (preserveBottom)
        {
            Top = bottom - ActualHeight;
        }

        ClampToScreen();
    }

    private void ToggleExpanded() => SetExpanded(!_state.Settings.OverlayExpanded);

    private void SetExpanded(bool expanded)
    {
        if (_state.Settings.OverlayExpanded == expanded)
        {
            return;
        }

        var bottom = Top + ActualHeight;
        _state.Settings.OverlayExpanded = expanded;
        ApplyExpanded(preserveBottom: false);
        Top = bottom - ActualHeight;
        ClampToScreen();
        SavePosition();
    }

    private void ApplyMinimizedLayout(bool minimized)
    {
        _adjustingLayout = true;
        try
        {
            ContentRoot.Visibility = minimized ? Visibility.Collapsed : Visibility.Visible;
            MinimizedPanel.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;
            Card.Width = minimized ? 320 : 404;
            RootHost.Margin = minimized ? new Thickness(0) : new Thickness(24);
            ShadowHost.Effect = minimized ? null : _cardShadow;
            UpdateLayout();
        }
        finally
        {
            _adjustingLayout = false;
        }
    }

    private void MinimizeOverlay()
    {
        if (_state.Settings.OverlayMinimized)
        {
            return;
        }

        var area = GetWorkAreaDip();
        var centerX = Left + ActualWidth / 2;
        var centerY = Top + ActualHeight / 2;
        var dockTop = centerY < area.Top + area.Height / 2;

        _state.Settings.OverlayRestoreLeft = Left;
        _state.Settings.OverlayRestoreTop = Top;
        _state.Settings.OverlayMinimized = true;

        ApplyMinimizedLayout(true);

        var width = ActualWidth;
        var height = ActualHeight;
        var left = centerX - width / 2;
        left = Math.Max(area.Left + 4, Math.Min(left, area.Right - width - 4));
        var top = dockTop ? area.Top + 4 : area.Bottom - height - 4;

        _restoring = true;
        Left = left;
        Top = top;
        _restoring = false;
        ClampToScreen();
        SavePosition();
    }

    private void RestoreOverlay()
    {
        if (!_state.Settings.OverlayMinimized)
        {
            return;
        }

        _state.Settings.OverlayMinimized = false;
        ApplyMinimizedLayout(false);

        var restoreLeft = _state.Settings.OverlayRestoreLeft;
        var restoreTop = _state.Settings.OverlayRestoreTop;
        if (restoreLeft is { } left && restoreTop is { } top && IsOnScreen(left, top))
        {
            _restoring = true;
            Left = left;
            Top = top;
            _restoring = false;
            ClampToScreen();
        }
        else
        {
            PlaceBottomRight();
        }

        SavePosition();
    }

    private void ApplyClickThrough()
    {
        WindowEffects.SetClickThrough(this, _state.Settings.OverlayClickThrough);
        Card.Cursor = _state.Settings.OverlayLocked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void RestorePosition()
    {
        var settings = _state.Settings;
        if (settings.OverlayLeft is { } left && settings.OverlayTop is { } top && IsOnScreen(left, top))
        {
            _restoring = true;
            Left = left;
            Top = top;
            _restoring = false;
            ClampToScreen();
            return;
        }

        PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        var area = GetWorkAreaDip();
        _restoring = true;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
        _restoring = false;
        ClampToScreen();
        SavePosition();
    }

    private bool IsOnScreen(double left, double top)
    {
        var area = GetWorkAreaDip();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        return left + 60 < area.Right && left + width - 60 > area.Left &&
               top + 40 < area.Bottom && top + height - 40 > area.Top;
    }

    private void ClampToScreen()
    {
        if (!_loaded && ActualWidth <= 0)
        {
            return;
        }

        var area = GetWorkAreaDip();
        const double margin = 4;
        var maxLeft = area.Right - ActualWidth - margin;
        var maxTop = area.Bottom - ActualHeight - margin;
        var newLeft = Math.Max(area.Left + margin, Math.Min(Left, maxLeft));
        var newTop = Math.Max(area.Top + margin, Math.Min(Top, maxTop));

        if (double.IsNaN(newLeft) || double.IsNaN(newTop))
        {
            return;
        }

        if (Math.Abs(newLeft - Left) > 0.5 || Math.Abs(newTop - Top) > 0.5)
        {
            _restoring = true;
            Left = newLeft;
            Top = newTop;
            _restoring = false;
        }
    }

    private Rect GetWorkAreaDip()
    {
        WinForms.Screen screen;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            screen = WinForms.Screen.FromHandle(hwnd);
        }
        else
        {
            screen = WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        return new Rect(
            area.Left / dpi.DpiScaleX,
            area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX,
            area.Height / dpi.DpiScaleY);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_loaded || _restoring)
        {
            return;
        }

        _positionTimer.Stop();
        _positionTimer.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_loaded || _restoring || _adjustingLayout || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
        {
            return;
        }

        // Keep the bottom edge anchored when the card grows or shrinks
        // (article name toggled, scale changed, expanded panel).
        var bottom = Top + e.PreviousSize.Height;
        var newTop = bottom - e.NewSize.Height;
        if (!double.IsNaN(newTop))
        {
            _restoring = true;
            Top = newTop;
            _restoring = false;
        }

        ClampToScreen();
        SavePosition();
    }

    private void SavePosition()
    {
        if (!_loaded || _restoring)
        {
            return;
        }

        _state.Settings.OverlayLeft = Left;
        _state.Settings.OverlayTop = Top;
    }

    // ---------------------------------------------------------------- updates

    private void UpdateToggleGlyphs()
    {
        var showPlay = !_state.Engine.IsStarted || _state.Engine.IsPaused;
        var glyph = showPlay ? "\uE768" : "\uE769";
        if (!Equals(PauseButton.Content, glyph))
        {
            PauseButton.Content = glyph;
            PauseButton.ToolTip = !_state.Engine.IsStarted ? "开始计时" : showPlay ? "继续计时" : "暂停计时";
        }

        var largeText = showPlay ? "继续" : "暂停";
        if (!Equals(PauseButtonLarge.Content, largeText))
        {
            PauseButtonLarge.Content = largeText;
        }

        var locked = _state.Settings.OverlayLocked;
        var lockGlyph = locked ? "\uE72E" : "\uE785";
        if (!Equals(LockButton.Content, lockGlyph))
        {
            LockButton.Content = lockGlyph;
            LockButton.ToolTip = locked ? "已锁定 · 点击解锁" : "锁定位置";
            Card.Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.OverlayExpanded):
                ApplyExpanded(preserveBottom: true);
                SavePosition();
                break;
            case nameof(AppSettings.OverlayScale):
                ApplyScale();
                SavePosition();
                break;
            case nameof(AppSettings.OverlayClickThrough):
                ApplyClickThrough();
                break;
            case nameof(AppSettings.OverlayLocked):
                UpdateToggleGlyphs();
                ApplyClickThrough();
                break;
        }
    }

    // ---------------------------------------------------------------- commands

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _state.Engine.TogglePause();
        UpdateToggleGlyphs();
    }

    private void BreakButton_Click(object sender, RoutedEventArgs e)
    {
        _state.Engine.BeginBreak();
        UpdateToggleGlyphs();
    }

    private void ShowMainButton_Click(object sender, RoutedEventArgs e) => _state.RequestShowMain();

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.OverlayLocked = !_state.Settings.OverlayLocked;
        UpdateToggleGlyphs();
        ApplyClickThrough();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeOverlay();

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreOverlay();

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => SetExpanded(!_state.Settings.OverlayExpanded);

    private void ArticleTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        ShowTitleEditor();
    }

    private void ShowTitleEditor()
    {
        if (_titleEditorOpen)
        {
            return;
        }

        _titleEditorOpen = true;
        try
        {
            var dialog = new TitleEditWindow(_state.CurrentDocumentName)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var left = Left + Math.Max(8, (ActualWidth - dialog.Width) / 2);
            var top = Top + 24;
            dialog.Left = Math.Max(virtualLeft + 8, Math.Min(left, virtualLeft + virtualWidth - dialog.Width - 8));
            dialog.Top = Math.Max(virtualTop + 8, Math.Min(top, virtualTop + virtualHeight - 160));

            if (dialog.ShowDialog() == true && dialog.Saved)
            {
                _state.SetCurrentDocumentCustomTitle(dialog.Result);
            }
        }
        finally
        {
            _titleEditorOpen = false;
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideOverlay();

    private void HideOverlay()
    {
        _state.Settings.ShowOverlay = false;
        (Application.Current as App)?.ShowToast("浮窗已隐藏", $"按 {_state.OverlayHotkeyText} 可以随时重新显示。", ToastKind.Info);
    }

    // ---------------------------------------------------------------- dragging

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // In the minimized strip, double-clicking should not expand/restore.
            if (!_state.Settings.OverlayMinimized)
            {
                ToggleExpanded();
            }

            e.Handled = true;
            return;
        }

        if (_state.Settings.OverlayLocked || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragStartCursor = GetCursorDip();
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragging = true;
        Card.CaptureMouse();
        e.Handled = true;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = GetCursorDip();
        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X);
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Card.ReleaseMouseCapture();
        ClampToScreen();
        SavePosition();
    }

    private Point GetCursorDip()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return GetCursorPos(out var point)
            ? new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY)
            : new Point(Left, Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);
}
