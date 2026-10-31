using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InkFlow.Core;
using InkFlow.Models;
using InkFlow.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace InkFlow.Views;

public partial class MainWindow : Window
{
    private const string PreviewSampleText =
        "雨落在旧城的瓦上，像有人轻轻翻动一本很厚的书。\n" +
        "她把窗推开一条缝，潮湿的风涌进来，带着桂花的甜。桌上的台灯亮着，稿纸摊开到第七页，墨迹还没干透。\n" +
        "故事从这里开始——一个人，一间屋，一场不肯停的雨。\n" +
        "她写下第一句话，又把它划掉。第二句更好一些，于是她继续写下去。\n" +
        "有些句子需要在心里放很久，等它自己长出形状。写作大概就是这样的事：耐心地等，诚恳地写，然后删掉那些不诚实的部分。";

    private readonly AppState _state;
    private readonly DispatcherTimer _countTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    private string _documentPath = string.Empty;
    private string _lastSavedText = string.Empty;
    private bool _loadingDocument;
    private bool _focusMode;
    private WindowState _preFocusWindowState = WindowState.Normal;
    private bool _closeHintShown;
    private DateTime _lastSaveTime = DateTime.Now;
    private bool _initialPlacementRestored;
    private bool _maximizedOnStart;
    private IntPtr _hwnd;
    private bool _isImeComposing;

    public MainWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        DataContext = state;

        _countTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _countTimer.Tick += CountTimer_Tick;

        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            if (IsImeComposing())
            {
                // Never persist a half-composed IME string.
                _autoSaveTimer.Start();
                return;
            }

            SaveDocument(false);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        _state.Settings.PropertyChanged += Settings_PropertyChanged;

        // WPF's RichTextBox inserts the IME composition string into the document.
        // TextCompositionManager is the reliable way to tell it apart from committed text.
        Editor.AddHandler(
            TextCompositionManager.TextInputStartEvent,
            new TextCompositionEventHandler(Editor_CompositionStart),
            true);
        Editor.AddHandler(
            TextCompositionManager.TextInputUpdateEvent,
            new TextCompositionEventHandler(Editor_CompositionUpdate),
            true);
        Editor.AddHandler(
            TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(Editor_CompositionEnd),
            true);

        ApplySavedPlacement();
        ApplyEditorTypography();
        UpdateThemeGlyph();
        UpdatePlaceholder();
        Opacity = 0;
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.EnableRoundedCorners(this);
        WindowEffects.SetDarkFrame(this, _state.IsDarkTheme);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WindowProc);
    }

    private bool IsImeComposing() => _isImeComposing || (_hwnd != IntPtr.Zero && ImeComposition.IsComposing(_hwnd));

    private void Editor_CompositionStart(object sender, TextCompositionEventArgs e)
    {
        _isImeComposing = true;
        _countTimer.Stop();
        _autoSaveTimer.Stop();
        PlaceholderText.Visibility = Visibility.Collapsed;
    }

    private void Editor_CompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        _isImeComposing = true;
        _countTimer.Stop();
        _autoSaveTimer.Stop();
        PlaceholderText.Visibility = Visibility.Collapsed;
    }

    private void Editor_CompositionEnd(object sender, TextCompositionEventArgs e)
    {
        _isImeComposing = false;
        _countTimer.Stop();
        _countTimer.Start();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    /// <summary>
    /// WindowChrome maximises a borderless window to the full monitor, which puts
    /// the bottom of the editor underneath the taskbar. Constrain it to the work area.
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var screen = WinForms.Screen.FromHandle(hwnd);
            var work = screen.WorkingArea;
            var bounds = screen.Bounds;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxPosition.X = work.Left - bounds.Left;
            info.MaxPosition.Y = work.Top - bounds.Top;
            info.MaxSize.X = work.Width;
            info.MaxSize.Y = work.Height;
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialPlacementRestored)
        {
            _initialPlacementRestored = true;
            if (_maximizedOnStart)
            {
                WindowState = WindowState.Maximized;
            }
        }

        var startupPath = ResolveStartupDocument();
        LoadDocument(startupPath);
        Editor.Focus();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        _lastSaveTime = DateTime.Now;
    }

    private string ResolveStartupDocument()
    {
        var settings = _state.Settings;
        if (!string.IsNullOrWhiteSpace(settings.LastDocumentPath))
        {
            return settings.LastDocumentPath;
        }

        var directory = string.IsNullOrWhiteSpace(settings.DraftDirectory)
            ? AppPaths.DefaultDraftDirectory
            : settings.DraftDirectory;
        return Path.Combine(directory, "我的草稿.md");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _countTimer.Stop();
        _autoSaveTimer.Stop();
        SaveDocument(false);
        SavePlacement();

        var app = Application.Current as App;
        if (app?.IsExiting == true)
        {
            return;
        }

        if (_state.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_closeHintShown)
            {
                _closeHintShown = true;
                app?.ShowToast("墨流仍在后台运行", "浮窗和计时不会停。双击托盘图标，或按 Ctrl+Alt+M 随时回来。", ToastKind.Info);
            }
        }
        else
        {
            e.Cancel = true;
            app?.RequestExit();
        }
    }

    public void PrepareForExit()
    {
        _countTimer.Stop();
        _autoSaveTimer.Stop();
        SaveDocument(false);
        SavePlacement();
        _state.Settings.PropertyChanged -= Settings_PropertyChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        Frame.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(10);
    }

    private void ApplySavedPlacement()
    {
        var settings = _state.Settings;
        if (settings.MainWidth is > 0 && settings.MainHeight is > 0)
        {
            Width = Math.Max(MinWidth, settings.MainWidth.Value);
            Height = Math.Max(MinHeight, settings.MainHeight.Value);
        }

        if (settings.MainLeft is { } left && settings.MainTop is { } top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        _maximizedOnStart = settings.MainMaximized;
    }

    private static bool IsOnScreen(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        return left + 120 > virtualLeft && left < virtualLeft + virtualWidth - 120 &&
               top + 80 > virtualTop && top < virtualTop + virtualHeight - 80;
    }

    private void SavePlacement()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var settings = _state.Settings;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        settings.MainMaximized = WindowState == WindowState.Maximized;
        if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100)
        {
            return;
        }

        settings.MainLeft = bounds.Left;
        settings.MainTop = bounds.Top;
        settings.MainWidth = bounds.Width;
        settings.MainHeight = bounds.Height;
    }

    // ---------------------------------------------------------------- document

    private void LoadDocument(string path)
    {
        _countTimer.Stop();
        _autoSaveTimer.Stop();

        var text = string.Empty;
        var exists = false;
        try
        {
            if (File.Exists(path))
            {
                text = File.ReadAllText(path);
                exists = true;
            }
            else
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }
        catch (Exception ex)
        {
            _state.SetSaveStatus("读取失败", false);
            ShowToastSafe("无法打开文稿", ex.Message, ToastKind.Warning);
        }

        _documentPath = path;
        SetEditorText(text);
        ApplyEditorTypography();

        var normalized = GetEditorText();
        var count = TextMetrics.CountCharacters(normalized);
        var paragraphs = TextMetrics.CountParagraphs(normalized);
        _lastSavedText = normalized;
        _state.SetDocument(path);
        _state.Settings.LastDocumentPath = path;
        _state.Engine.ResetDocumentBaseline(count);
        _state.UpdateDocumentMetrics(count, paragraphs, string.IsNullOrWhiteSpace(normalized));
        _state.SetSaveStatus(exists ? $"已保存 {DateTime.Now:HH:mm}" : "新文稿", true);
        UpdatePlaceholder();
    }

    private void SetEditorText(string text)
    {
        _loadingDocument = true;
        try
        {
            Editor.IsUndoEnabled = false;
            Editor.Document.Blocks.Clear();
            Editor.Document.PagePadding = new Thickness(0);
            if (!string.IsNullOrEmpty(text))
            {
                var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
                range.Text = TextMetrics.ToWindowsNewlines(text);
            }

            Editor.IsUndoEnabled = true;
            Editor.CaretPosition = Editor.Document.ContentEnd;
        }
        finally
        {
            _loadingDocument = false;
        }
    }

    private string GetEditorText()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        return range.Text.TrimEnd('\r', '\n');
    }

    private void ApplyEditorTypography()
    {
        var fontSize = _state.Settings.EditorFontSize;
        var lineHeight = Math.Ceiling(fontSize * 1.82);
        var previous = _loadingDocument;

        // Changing paragraph formatting makes RichTextBox raise TextChanged,
        // which must never be mistaken for typing.
        _loadingDocument = true;
        try
        {
            Editor.Document.PagePadding = new Thickness(0);
            foreach (var block in Editor.Document.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    paragraph.Margin = new Thickness(0);
                    paragraph.LineHeight = lineHeight;
                    paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                }
            }
        }
        finally
        {
            _loadingDocument = previous;
        }
    }

    private void UpdatePlaceholder()
    {
        PlaceholderText.Visibility = _state.IsDocumentEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDocument)
        {
            return;
        }

        _state.Engine.NoteActivity();
        _state.SetSaveStatus("未保存", false);
        PlaceholderText.Visibility = Visibility.Collapsed;

        _countTimer.Stop();
        _countTimer.Start();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();

        if ((DateTime.Now - _lastSaveTime).TotalSeconds > 15 && !IsImeComposing())
        {
            SaveDocument(false);
        }
    }

    private void CountTimer_Tick(object? sender, EventArgs e)
    {
        _countTimer.Stop();

        if (IsImeComposing())
        {
            // The pinyin/candidate string is not committed text yet.
            // Keep the editor visually clean but do not count or persist it.
            PlaceholderText.Visibility = Visibility.Collapsed;
            _countTimer.Start();
            return;
        }

        var text = GetEditorText();
        var count = TextMetrics.CountCharacters(text);
        var paragraphs = TextMetrics.CountParagraphs(text);
        _state.Engine.UpdateDocumentCount(count);
        _state.UpdateDocumentMetrics(count, paragraphs, string.IsNullOrWhiteSpace(text));
        UpdatePlaceholder();
    }

    private bool SaveDocument(bool force)
    {
        if (IsImeComposing())
        {
            _state.SetSaveStatus("输入法组字中", false);
            return false;
        }

        try
        {
            var text = GetEditorText();
            if (!force && !_state.HasUnsavedChanges && string.Equals(text, _lastSavedText, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(_documentPath))
            {
                return SaveAsDocument();
            }

            var directory = Path.GetDirectoryName(_documentPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_documentPath, text, new UTF8Encoding(false));
            _lastSavedText = text;
            _lastSaveTime = DateTime.Now;
            _state.SetSaveStatus($"已保存 {DateTime.Now:HH:mm}", true);
            _state.Settings.LastDocumentPath = _documentPath;
            return true;
        }
        catch (Exception ex)
        {
            _state.SetSaveStatus("保存失败", false);
            ShowToastSafe("保存失败", ex.Message, ToastKind.Warning);
            return false;
        }
    }

    private bool SaveAsDocument()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存文稿",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_state.DocumentTitle) ? "未命名文稿" : _state.DocumentTitle,
            InitialDirectory = Directory.Exists(_state.Settings.DraftDirectory) ? _state.Settings.DraftDirectory : null,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        _documentPath = dialog.FileName;
        _state.SetDocument(_documentPath);
        _state.Settings.LastDocumentPath = _documentPath;
        return SaveDocument(true);
    }

    private void OpenDocument()
    {
        SaveDocument(false);
        var dialog = new OpenFileDialog
        {
            Title = "打开文稿",
            Filter = "支持的文件 (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_state.Settings.DraftDirectory) ? _state.Settings.DraftDirectory : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadDocument(dialog.FileName);
            Editor.Focus();
        }
    }

    private void NewDocument()
    {
        SaveDocument(false);
        var dialog = new SaveFileDialog
        {
            Title = "新建文稿",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = "未命名文稿",
            InitialDirectory = Directory.Exists(_state.Settings.DraftDirectory) ? _state.Settings.DraftDirectory : null,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, string.Empty, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            ShowToastSafe("无法创建文稿", ex.Message, ToastKind.Warning);
            return;
        }

        LoadDocument(dialog.FileName);
        Editor.Focus();
    }

    private void ShowToastSafe(string title, string message, ToastKind kind)
    {
        (Application.Current as App)?.ShowToast(title, message, kind);
    }

    // ---------------------------------------------------------------- commands

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveDocument(true);

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenDocument();

    private void FocusButton_Click(object sender, RoutedEventArgs e) => SetFocusMode(true);

    private void ExitFocus_Click(object sender, RoutedEventArgs e) => SetFocusMode(false);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse was released before the drag started.
            }
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not FrameworkElement element)
        {
            return;
        }

        if (int.TryParse(element.Tag?.ToString(), out var page))
        {
            ShowPage(page);
        }
    }

    private void ShowPage(int index)
    {
        PageWrite.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageStats.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        NavWrite.IsChecked = index == 0;
        NavStats.IsChecked = index == 1;
        NavSettings.IsChecked = index == 2;

        if (index == 0)
        {
            Editor.Focus();
        }
    }

    public void ShowPageForPreview(int index) => ShowPage(index);

    public void NavigateTo(int index) => ShowPage(index);

    // ---------------------------------------------------------------- focus mode

    private void SetFocusMode(bool enabled)
    {
        if (_focusMode == enabled)
        {
            return;
        }

        _focusMode = enabled;
        if (enabled)
        {
            _preFocusWindowState = WindowState;
            NavWrite.IsChecked = true;
            TitleRow.Height = new GridLength(0);
            TitleBar.Visibility = Visibility.Collapsed;
            SideColumn.Width = new GridLength(0);
            SidePanel.Visibility = Visibility.Collapsed;
            EditorCard.Margin = new Thickness(0);
            PageWrite.Margin = new Thickness(0);
            FocusExitButton.Visibility = Visibility.Visible;
            FocusHint.Visibility = Visibility.Visible;
            if (WindowState != WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            Editor.Focus();
        }
        else
        {
            TitleRow.Height = new GridLength(66);
            TitleBar.Visibility = Visibility.Visible;
            SideColumn.Width = new GridLength(344);
            SidePanel.Visibility = Visibility.Visible;
            EditorCard.Margin = new Thickness(0, 0, 14, 0);
            PageWrite.Margin = new Thickness(18, 0, 18, 18);
            FocusExitButton.Visibility = Visibility.Collapsed;
            FocusHint.Visibility = Visibility.Collapsed;
            WindowState = _preFocusWindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Editor.Focus();
        }
    }

    // ---------------------------------------------------------------- settings

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                UpdateThemeGlyph();
                break;
            case nameof(AppSettings.EditorFontSize):
            case nameof(AppSettings.EditorFont):
                ApplyEditorTypography();
                break;
        }
    }

    private void UpdateThemeGlyph()
    {
        ThemeGlyph.Text = _state.IsDarkTheme ? "\uE706" : "\uE708";
        ThemeGlyph.ToolTip = _state.IsDarkTheme ? "切换到浅色主题" : "切换到深色主题";
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
        => _state.SetTheme(_state.IsDarkTheme ? "Light" : "Dark");

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
        => _state.Settings.ShowOverlay = !_state.Settings.ShowOverlay;

    private void ThemeChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string theme)
        {
            _state.SetTheme(theme);
        }
    }

    private void GoalChip_Click(object sender, RoutedEventArgs e)
        => ApplyIntTag(sender, value => _state.Settings.DailyGoal = value);

    private void BreakChip_Click(object sender, RoutedEventArgs e)
        => ApplyIntTag(sender, value => _state.Settings.BreakReminderMinutes = value);

    private void IdleChip_Click(object sender, RoutedEventArgs e)
        => ApplyIntTag(sender, value => _state.Settings.IdleThresholdSeconds = value);

    private void AutoPauseChip_Click(object sender, RoutedEventArgs e)
        => ApplyIntTag(sender, value => _state.Settings.AutoPauseMinutes = value);

    private static void ApplyIntTag(object sender, Action<int> apply)
    {
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out var value))
        {
            apply(value);
        }
    }

    private void FontChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string font)
        {
            _state.Settings.EditorFont = font;
            ApplyEditorTypography();
        }
    }

    private void GoalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(GoalBox.Text, out var value))
        {
            _state.Settings.DailyGoal = Math.Clamp(value, 0, 200_000);
        }

        GoalBox.Text = _state.Settings.DailyGoal.ToString();
    }

    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(c => !char.IsDigit(c));
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _state.OpenDataFolder();

    private void OpenDraftFolder_Click(object sender, RoutedEventArgs e) => _state.OpenDraftFolder();

    private void ChangeDraftFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择草稿目录",
        };

        if (Directory.Exists(_state.Settings.DraftDirectory))
        {
            dialog.InitialDirectory = _state.Settings.DraftDirectory;
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _state.Settings.DraftDirectory = dialog.FolderName;
        }
    }

    private void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _state.Engine.StartNewSession();
        ShowToastSafe("新的一节", "本节计数已归零，今日累计保持不变。", ToastKind.Info);
    }

    private void ResetSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "重置本节会清零本节的字数、时间与速度统计，今日累计不会减少。继续吗？",
            "重置本节", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            _state.Engine.StartNewSession();
        }
    }

    private void ClearTodayButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "这会清空今天的全部字数、时长与休息记录，且无法恢复。确定要清空吗？",
            "清空今日数据", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            _state.Engine.ResetToday();
            ShowToastSafe("今日数据已清空", "从下一秒开始，重新记录。", ToastKind.Info);
        }
    }

    // ---------------------------------------------------------------- input

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.F11)
        {
            SetFocusMode(!_focusMode);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _focusMode)
        {
            SetFocusMode(false);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
        {
            if (shift)
            {
                SaveAsDocument();
            }
            else
            {
                SaveDocument(true);
            }

            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.O)
        {
            OpenDocument();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.N)
        {
            NewDocument();
            e.Handled = true;
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _focusMode)
        {
            SetFocusMode(false);
            e.Handled = true;
        }
    }

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var delta = e.Delta > 0 ? 0.5 : -0.5;
        _state.Settings.EditorFontSize = Math.Clamp(_state.Settings.EditorFontSize + delta, 13, 26);
        ApplyEditorTypography();
        e.Handled = true;
    }

    private void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        var file = files[0];
        if (!File.Exists(file))
        {
            return;
        }

        SaveDocument(false);
        LoadDocument(file);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- preview

    internal void PopulatePreviewDocument()
    {
        SetEditorText(PreviewSampleText);
        ApplyEditorTypography();
        var normalized = GetEditorText();
        _lastSavedText = normalized;
        var count = TextMetrics.CountCharacters(normalized);
        _state.Engine.ResetDocumentBaseline(count);
        _state.UpdateDocumentMetrics(count, TextMetrics.CountParagraphs(normalized), false);
        _state.SetSaveStatus($"已保存 {DateTime.Now:HH:mm}", true);
        UpdatePlaceholder();
    }
}
