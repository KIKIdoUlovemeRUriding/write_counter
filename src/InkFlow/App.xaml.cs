using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkFlow.Models;
using InkFlow.Services;
using InkFlow.Views;

namespace InkFlow;

public partial class App : Application
{
    private const string ShowMessageName = "InkFlow.ShowMainWindow";
    private const int HotKeyToggleOverlay = 0x411;
    private const int HotKeyToggleClickThrough = 0x412;
    private const int HotKeyTogglePause = 0x413;
    private const int HotKeyShowMain = 0x414;

    private Mutex? _mutex;
    private uint _showMessage;
    private HwndSource? _messageSource;
    private HotKeyManager? _hotkeys;
    private TrayService? _tray;
    private ToastWindow? _toast;
    private string? _previewDir;
    private bool _exitHandled;
    private bool _hotkeyFallbackNotified;

    public AppState State { get; private set; } = null!;

    public MainWindow MainHost { get; private set; } = null!;

    public OverlayWindow Overlay { get; private set; } = null!;

    public bool IsExiting { get; private set; }

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.ExceptionObject as Exception);
        };
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(
                Path.Combine(AppPaths.DataDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing useful to do if even logging fails.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\InkFlow.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            NotifyExistingInstance();
            Shutdown();
            return;
        }

        _showMessage = RegisterWindowMessage(ShowMessageName);

        _previewDir = GetArg(e.Args, "--preview");
        var theme = GetArg(e.Args, "--theme");
        var page = GetArg(e.Args, "--page");

        if (_previewDir is not null)
        {
            SeedPreviewFiles();
        }

        State = new AppState();
        if (theme is "Light" or "Dark")
        {
            State.Settings.Theme = theme;
            State.ApplyTheme();
        }

        State.ShowMainRequested += ShowMainWindow;
        State.ExitRequested += RequestExit;
        State.Engine.BreakReminderDue += OnBreakReminderDue;
        State.Engine.GoalReached += OnGoalReached;
        State.SettingsService.SettingChanged += OnSettingChanged;

        MainHost = new MainWindow(State);
        MainHost.Show();

        Overlay = new OverlayWindow(State);
        if (State.Settings.ShowOverlay)
        {
            Overlay.EnsureVisible();
        }

        _tray = new TrayService(State, ShowMainWindow, ShowSettings, RequestExit);
        _tray.Initialize();

        _hotkeys = new HotKeyManager(MainHost);
        if (State.Settings.HotkeysEnabled)
        {
            RegisterHotkeys();
        }

        HookShowMessage();

        if (!State.Settings.WelcomeShown && _previewDir is null)
        {
            State.Settings.WelcomeShown = true;
            ShowToast("欢迎使用墨流", "敲下第一个字，计时与字数统计会自动开始。常驻的小浮窗会一直陪着你。", ToastKind.Info);
        }

        if (_previewDir is not null)
        {
            _ = RunPreviewAsync(_previewDir, page);
        }
    }

    // ---------------------------------------------------------------- windows

    public void ShowMainWindow()
    {
        if (MainHost is null)
        {
            return;
        }

        WindowEffects.BringToFront(MainHost);
        MainHost.Activate();
        MainHost.Focus();
    }

    private void ShowSettings()
    {
        ShowMainWindow();
        MainHost.NavigateTo(2);
    }

    public void ShowToast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        try
        {
            _toast ??= new ToastWindow();
            var above = Overlay is { IsVisible: true } ? Overlay.Top : (double?)null;
            _toast.ShowToast(title, message, kind, above);
        }
        catch
        {
            // Notifications are decorative; never let them break the app.
        }
    }

    // ---------------------------------------------------------------- hotkeys

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        _hotkeys.UnregisterAll();

        // Ctrl+Alt+W is the memorable default, but many editors grab it.
        // Fall back gracefully and tell the user which combination is live.
        var candidates = new (ModifierKeys Modifiers, Key Key, string Label)[]
        {
            (ModifierKeys.Control | ModifierKeys.Alt, Key.W, "Ctrl+Alt+W"),
            (ModifierKeys.Control | ModifierKeys.Alt, Key.O, "Ctrl+Alt+O"),
            (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt, Key.W, "Ctrl+Shift+Alt+W"),
        };

        string? overlayLabel = null;
        var failedOverlayLabels = new List<string>();
        foreach (var candidate in candidates)
        {
            if (_hotkeys.Register(HotKeyToggleOverlay, candidate.Modifiers, candidate.Key, candidate.Label, ToggleOverlay))
            {
                overlayLabel = candidate.Label;
                break;
            }

            failedOverlayLabels.Add(candidate.Label);
        }

        if (overlayLabel is not null)
        {
            State.OverlayHotkeyText = overlayLabel;
            foreach (var label in failedOverlayLabels)
            {
                _hotkeys.Failures.Remove(label);
            }
        }
        else
        {
            State.OverlayHotkeyText = "未注册";
        }

        _hotkeys.Register(HotKeyToggleClickThrough, ModifierKeys.Control | ModifierKeys.Alt, Key.T,
            "Ctrl+Alt+T 鼠标穿透", ToggleClickThrough);
        _hotkeys.Register(HotKeyTogglePause, ModifierKeys.Control | ModifierKeys.Alt, Key.Space,
            "Ctrl+Alt+Space 暂停/继续", () => State.Engine.TogglePause());
        _hotkeys.Register(HotKeyShowMain, ModifierKeys.Control | ModifierKeys.Alt, Key.M,
            "Ctrl+Alt+M 打开主界面", ShowMainWindow);

        State.HotkeyWarningText = _hotkeys.Failures.Count == 0
            ? string.Empty
            : "以下快捷键被其他程序占用：" + string.Join("、", _hotkeys.Failures);

        if (!_hotkeyFallbackNotified && overlayLabel is not null && overlayLabel != "Ctrl+Alt+W" &&
            _previewDir is null && !State.Settings.OverlayHotkeyNoticeShown)
        {
            _hotkeyFallbackNotified = true;
            State.Settings.OverlayHotkeyNoticeShown = true;
            ShowToast("浮窗快捷键已调整", $"{overlayLabel} 显示 / 隐藏浮窗（Ctrl+Alt+W 被占用）。", ToastKind.Info);
        }
    }

    private void ToggleOverlay()
    {
        State.Settings.ShowOverlay = !State.Settings.ShowOverlay;
    }

    private void ToggleClickThrough()
    {
        State.Settings.OverlayClickThrough = !State.Settings.OverlayClickThrough;
        if (State.Settings.OverlayClickThrough)
        {
            ShowToast("浮窗已穿透鼠标", "按 Ctrl+Alt+T 可以恢复鼠标操作。", ToastKind.Info);
        }
    }

    // ---------------------------------------------------------------- settings

    private void OnSettingChanged(object? sender, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(AppSettings.ShowOverlay):
                if (State.Settings.ShowOverlay)
                {
                    Overlay.EnsureVisible();
                }
                else
                {
                    Overlay.Hide();
                }

                break;

            case nameof(AppSettings.HotkeysEnabled):
                if (State.Settings.HotkeysEnabled)
                {
                    RegisterHotkeys();
                }
                else
                {
                    _hotkeys?.UnregisterAll();
                    State.HotkeyWarningText = string.Empty;
                }

                break;
        }
    }

    private void OnBreakReminderDue(object? sender, EventArgs e)
    {
        var minutes = Math.Max(1, State.Engine.ContinuousWritingSeconds / 60);
        ShowToast("该休息一下了", $"你已经连续码字 {minutes} 分钟。起身走走，看看远处，回来再写。", ToastKind.Rest);
        if (State.Settings.SoundEnabled)
        {
            PlayNotificationSound();
        }
    }

    private void OnGoalReached(object? sender, EventArgs e)
    {
        ShowToast("今日目标达成", $"今天已经写了 {State.Engine.WordsToday:N0} 字。可以收工，也可以趁手感再好一会儿。", ToastKind.Success);
        if (State.Settings.SoundEnabled)
        {
            PlayNotificationSound();
        }
    }

    private static void PlayNotificationSound()
    {
        try
        {
            SystemSounds.Asterisk.Play();
        }
        catch
        {
            // A machine without an audio device must not break the timer.
        }
    }

    // ---------------------------------------------------------------- shutdown

    public void RequestExit()
    {
        if (_exitHandled)
        {
            return;
        }

        _exitHandled = true;
        IsExiting = true;

        try
        {
            MainHost?.PrepareForExit();
            State?.Shutdown();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _toast?.Close();
            Overlay?.Close();
            MainHost?.Close();
        }
        catch
        {
            // Keep shutting down even if one piece fails.
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _messageSource?.RemoveHook(WndProc);
            _hotkeys?.Dispose();
            _tray?.Dispose();
        }
        catch
        {
            // Ignore cleanup failures.
        }

        try
        {
            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch
        {
            // The mutex may not be owned if startup was aborted.
        }

        base.OnExit(e);
    }

    // ---------------------------------------------------------------- single instance

    private void HookShowMessage()
    {
        var handle = new WindowInteropHelper(MainHost).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _messageSource = HwndSource.FromHwnd(handle);
        _messageSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_showMessage != 0 && msg == (int)_showMessage)
        {
            handled = true;
            ShowMainWindow();
        }

        return IntPtr.Zero;
    }

    private static void NotifyExistingInstance()
    {
        var message = RegisterWindowMessage(ShowMessageName);
        if (message != 0)
        {
            PostMessage(new IntPtr(0xFFFF), message, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------- preview harness

    private void SeedPreviewFiles()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var today = DateTime.Today;
            var wordSamples = new long[] { 1840, 2380, 960, 3120, 1680, 0, 2240, 2720, 1460, 1980, 3040, 880, 2360, 0 };
            var records = new List<DailyRecord>();
            for (var i = 13; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                var words = wordSamples[13 - i];
                records.Add(new DailyRecord
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    Words = i == 0 ? 0 : words,
                    WritingSeconds = i == 0 ? 0 : (long)(words / 42.0 * 60),
                    RestSeconds = i == 0 ? 0 : (long)(words / 42.0 * 60 * 0.38),
                    Breaks = i == 0 ? 0 : 3,
                    BestSpeed = i == 0 ? 0 : 58,
                });
            }

            File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(records, JsonDefaults.Options));

            var draftDirectory = Path.Combine(AppPaths.DataDir, "drafts");
            Directory.CreateDirectory(draftDirectory);
            var draftPath = Path.Combine(draftDirectory, "墨流 · 演示文稿.md");
            File.WriteAllText(draftPath, string.Empty);

            var settings = new AppSettings
            {
                Theme = "Dark",
                DailyGoal = 2000,
                DraftDirectory = draftDirectory,
                LastDocumentPath = draftPath,
                WelcomeShown = true,
                ShowOverlay = true,
                OverlayExpanded = false,
                OverlayBackgroundOpacity = 0.94,
                OverlayTextOpacity = 1.0,
            };
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        }
        catch
        {
            // Preview data is best effort.
        }
    }

    private async Task RunPreviewAsync(string directory, string? page)
    {
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(700);

            State.Settings.ShowOverlay = true;
            State.Settings.OverlayExpanded = false;
            await Task.Delay(150);

            MainHost.PopulatePreviewDocument();
            State.SeedPreviewSession();
            MainHost.ShowPageForPreview(0);
            await Task.Delay(450);

            Capture(MainHost, Path.Combine(directory, "main-write-dark.png"));

            MainHost.ShowPageForPreview(1);
            await Task.Delay(300);
            Capture(MainHost, Path.Combine(directory, "main-stats-dark.png"));

            MainHost.ShowPageForPreview(2);
            await Task.Delay(300);
            Capture(MainHost, Path.Combine(directory, "main-settings-dark.png"));

            MainHost.ShowPageForPreview(0);
            Overlay.EnsureVisible();
            State.Settings.OverlayExpanded = false;
            await Task.Delay(350);
            Capture(Overlay, Path.Combine(directory, "overlay-compact-dark.png"));

            State.Settings.OverlayExpanded = true;
            await Task.Delay(350);
            Capture(Overlay, Path.Combine(directory, "overlay-expanded-dark.png"));

            State.SetTheme("Light");
            await Task.Delay(450);
            MainHost.ShowPageForPreview(0);
            await Task.Delay(250);
            Capture(MainHost, Path.Combine(directory, "main-write-light.png"));

            MainHost.ShowPageForPreview(1);
            await Task.Delay(250);
            Capture(MainHost, Path.Combine(directory, "main-stats-light.png"));

            State.Settings.OverlayExpanded = false;
            await Task.Delay(300);
            Capture(Overlay, Path.Combine(directory, "overlay-compact-light.png"));

            if (page is not null && int.TryParse(page, out var pageIndex))
            {
                MainHost.ShowPageForPreview(pageIndex);
            }

            RequestExit();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "preview-error.txt"), ex.ToString());
            }
            catch
            {
                // Ignore.
            }

            RequestExit();
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
