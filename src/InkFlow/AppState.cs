using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using InkFlow.Core;
using InkFlow.Models;
using InkFlow.Services;
using InkFlow.Views;
using WinForms = System.Windows.Forms;

namespace InkFlow;

/// <summary>
/// Shared application state: settings, live statistics, document metadata,
/// chart data and the commands used by both windows.
/// </summary>
public sealed class AppState : ObservableObject
{
    private readonly List<DailyRecord> _recentSource = new();
    private readonly WordIntegrationService _wordIntegration;
    private readonly PlainTextIntegrationService _plainTextIntegration;
    private bool _keyboardHooked;
    private bool _isWordTrackingActive;
    private bool _isPlainTextTrackingActive;
    private string _plainTextDocumentTitle = string.Empty;
    private string _plainTextBadgeText = "TXT";

    private string _documentTitle = "我的草稿";
    private string _documentPath = string.Empty;
    private string _wordDocumentTitle = string.Empty;
    private int _documentChars;
    private int _paragraphCount;
    private bool _isDocumentEmpty = true;
    private string _saveStatusText = "尚未保存";
    private bool _hasUnsavedChanges;
    private string _hotkeyWarningText = string.Empty;
    private string _overlayHotkeyText = "Ctrl+Alt+W";
    private int _streakDays;
    private long _monthWords;
    private long _totalWords;
    private string _todayDateText = string.Empty;

    public AppState()
    {
        SettingsService = new SettingsService();
        History = new HistoryService();
        Engine = new StatsEngine(SettingsService, History);
        Keyboard = new KeyboardActivityMonitor();
        _wordIntegration = new WordIntegrationService(SettingsService, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _plainTextIntegration = new PlainTextIntegrationService(SettingsService, System.Windows.Threading.Dispatcher.CurrentDispatcher);

        Engine.Tick += (_, _) => OnEngineTick();
        Engine.PropertyChanged += OnEnginePropertyChanged;
        Engine.DayChanged += (_, _) => OnDayChanged();
        SettingsService.SettingChanged += OnSettingChanged;
        _wordIntegration.WordDelta += delta => Engine.ApplyExternalWordCount(delta);
        _wordIntegration.ForegroundChanged += active => IsWordTrackingActive = active && Settings.WordTracking;
        _wordIntegration.DocumentChanged += title => WordDocumentTitle = title;
        _plainTextIntegration.WordDelta += delta => Engine.ApplyExternalWordCount(delta);
        _plainTextIntegration.ActiveChanged += active => IsPlainTextTrackingActive = active && Settings.PlainTextTracking;
        _plainTextIntegration.DocumentChanged += (title, badge) =>
        {
            PlainTextDocumentTitle = title;
            PlainTextBadgeText = badge;
        };

        TogglePauseCommand = new RelayCommand(() => Engine.TogglePause());
        NewSessionCommand = new RelayCommand(() => Engine.StartNewSession());
        BeginBreakCommand = new RelayCommand(() => Engine.BeginBreak());
        ShowMainCommand = new RelayCommand(() => ShowMainRequested?.Invoke());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenDraftFolderCommand = new RelayCommand(OpenDraftFolder);
        ToggleOverlayCommand = new RelayCommand(() => Settings.ShowOverlay = !Settings.ShowOverlay);

        ConfigureKeyboard();
        TryAdoptLegacyManualTitle();
        RebuildBarSources();
        RefreshDerived();
        ApplyTheme();
        _wordIntegration.Start();
        _plainTextIntegration.Start();
    }

    public SettingsService SettingsService { get; }

    public AppSettings Settings => SettingsService.Settings;

    public HistoryService History { get; }

    public StatsEngine Engine { get; }

    public KeyboardActivityMonitor Keyboard { get; }

    public bool IsWordTrackingActive
    {
        get => _isWordTrackingActive;
        private set
        {
            if (SetProperty(ref _isWordTrackingActive, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    public bool IsPlainTextTrackingActive
    {
        get => _isPlainTextTrackingActive;
        private set
        {
            if (SetProperty(ref _isPlainTextTrackingActive, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    public string PlainTextDocumentTitle
    {
        get => _plainTextDocumentTitle;
        private set
        {
            if (SetProperty(ref _plainTextDocumentTitle, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    public string PlainTextBadgeText
    {
        get => _plainTextBadgeText;
        private set
        {
            if (SetProperty(ref _plainTextBadgeText, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    public ObservableCollection<DayBarViewModel> RecentDays { get; } = new();

    public ICommand TogglePauseCommand { get; }

    public ICommand NewSessionCommand { get; }

    public ICommand BeginBreakCommand { get; }

    public ICommand ShowMainCommand { get; }

    public ICommand ExitCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenDraftFolderCommand { get; }

    public ICommand ToggleOverlayCommand { get; }

    public event Action? ShowMainRequested;

    public event Action? ExitRequested;

    public event Action? ThemeChangedByUser;

    public string Version => "1.0.1";

    public string Theme => Settings.Theme;

    public bool IsDarkTheme => Settings.Theme == "Dark";

    // ------------------------------------------------------------ document

    public string DocumentTitle
    {
        get => _documentTitle;
        private set
        {
            if (SetProperty(ref _documentTitle, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    /// <summary>The active Word document's display name, read from Word's object model.</summary>
    public string WordDocumentTitle
    {
        get => _wordDocumentTitle;
        private set
        {
            if (SetProperty(ref _wordDocumentTitle, value))
            {
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    /// <summary>
    /// The name shown on the floating card: a custom display name for the
    /// current document when one is set, otherwise the Word / TXT / draft name.
    /// </summary>
    public string CurrentDocumentName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.OverlayManualTitle))
            {
                return Settings.OverlayManualTitle;
            }

            var key = CurrentDocumentKey;
            if (key is not null &&
                Settings.DocumentTitleOverrides.TryGetValue(key, out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            return ResolveSourceDocumentName();
        }
    }

    private string ResolveSourceDocumentName()
    {
        if (IsWordTrackingActive && !string.IsNullOrWhiteSpace(WordDocumentTitle))
        {
            return WordDocumentTitle;
        }

        if (IsPlainTextTrackingActive && !string.IsNullOrWhiteSpace(PlainTextDocumentTitle))
        {
            return PlainTextDocumentTitle;
        }

        return DocumentTitle;
    }

    private string? CurrentDocumentKey
    {
        get
        {
            if (IsWordTrackingActive && !string.IsNullOrWhiteSpace(WordDocumentTitle))
            {
                return "word:" + WordDocumentTitle;
            }

            if (IsPlainTextTrackingActive && !string.IsNullOrWhiteSpace(PlainTextDocumentTitle))
            {
                return "txt:" + PlainTextBadgeText + ":" + PlainTextDocumentTitle;
            }

            if (!string.IsNullOrWhiteSpace(DocumentPath))
            {
                return "editor:" + DocumentPath;
            }

            return "editor:untitled:" + DocumentTitle;
        }
    }

    /// <summary>
    /// Sets the sticky floating-card title. While set, automatic document title
    /// changes are ignored. Empty text clears it and resumes following documents.
    /// </summary>
    public void SetCurrentDocumentCustomTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length > 60)
        {
            trimmed = trimmed[..60];
        }

        Settings.OverlayManualTitle = trimmed;

        if (trimmed.Length == 0 && Settings.DocumentTitleOverrides.Count > 0)
        {
            // Legacy per-document values must not resurrect the old manual title.
            Settings.DocumentTitleOverrides = new Dictionary<string, string>();
        }

        OnPropertyChanged(nameof(CurrentDocumentName));
        OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
    }

    private void TryAdoptLegacyManualTitle()
    {
        if (!string.IsNullOrWhiteSpace(Settings.OverlayManualTitle))
        {
            return;
        }

        var lastPath = Settings.LastDocumentPath;
        if (!string.IsNullOrWhiteSpace(lastPath) &&
            Settings.DocumentTitleOverrides.TryGetValue("editor:" + lastPath, out var lastTitle) &&
            !string.IsNullOrWhiteSpace(lastTitle))
        {
            Settings.OverlayManualTitle = lastTitle;
            return;
        }

        // Older builds could hold one manual title without a path match.
        if (Settings.DocumentTitleOverrides.Count == 1)
        {
            foreach (var title in Settings.DocumentTitleOverrides.Values)
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Settings.OverlayManualTitle = title;
                }

                break;
            }
        }
    }

    public bool ShowOverlayDocumentTitle =>
        Settings.ShowOverlayDocumentName && !string.IsNullOrWhiteSpace(CurrentDocumentName);

    public string DocumentPath
    {
        get => _documentPath;
        private set
        {
            if (SetProperty(ref _documentPath, value))
            {
                OnPropertyChanged(nameof(DocumentFolder));
                OnPropertyChanged(nameof(CurrentDocumentName));
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
            }
        }
    }

    public string DocumentFolder
    {
        get
        {
            try
            {
                return string.IsNullOrEmpty(DocumentPath) ? string.Empty : Path.GetDirectoryName(DocumentPath) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public int DocumentChars
    {
        get => _documentChars;
        private set => SetProperty(ref _documentChars, value);
    }

    public int ParagraphCount
    {
        get => _paragraphCount;
        private set => SetProperty(ref _paragraphCount, value);
    }

    public bool IsDocumentEmpty
    {
        get => _isDocumentEmpty;
        private set => SetProperty(ref _isDocumentEmpty, value);
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    // ------------------------------------------------------------ statistics

    public int StreakDays
    {
        get => _streakDays;
        private set => SetProperty(ref _streakDays, value);
    }

    public long MonthWords
    {
        get => _monthWords;
        private set => SetProperty(ref _monthWords, value);
    }

    public long TotalWords
    {
        get => _totalWords;
        private set => SetProperty(ref _totalWords, value);
    }

    public string TodayDateText
    {
        get => _todayDateText;
        private set => SetProperty(ref _todayDateText, value);
    }

    public string GoalCaption => $"今日 {Engine.WordsToday:N0} / {Math.Max(0, Engine.DailyGoal):N0} 字";

    public string GoalRemainingText => Engine.DailyGoal <= 0
        ? "未设置每日目标"
        : Engine.GoalRemaining > 0
            ? $"还差 {Engine.GoalRemaining:N0} 字"
            : "今日目标已达成";

    public string StreakText => StreakDays > 0 ? $"连续 {StreakDays} 天" : "从今天开始";

    public string MonthWordsText => $"{MonthWords:N0}";

    public string TotalWordsText => $"{TotalWords:N0}";

    public string HotkeyWarningText
    {
        get => _hotkeyWarningText;
        set => SetProperty(ref _hotkeyWarningText, value);
    }

    public string OverlayHotkeyText
    {
        get => _overlayHotkeyText;
        set => SetProperty(ref _overlayHotkeyText, value);
    }

    public string KeyboardWarningText => Keyboard.IsRunning
        ? string.Empty
        : Settings.GlobalTracking
            ? "全局按键监听未能启动（快捷键可能被占用）"
            : string.Empty;

    public string DataDirectory => AppPaths.DataDir;

    public string DraftDirectory => Settings.DraftDirectory;

    public string DocumentCountText => $"本文档 {DocumentChars:N0} 字";

    public string ParagraphText => $"{ParagraphCount:N0} 段";

    // ------------------------------------------------------------ operations

    public void SetDocument(string path)
    {
        DocumentPath = path;
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            DocumentTitle = string.IsNullOrWhiteSpace(name) ? "未命名文稿" : name;
        }
        catch
        {
            DocumentTitle = "未命名文稿";
        }
    }

    public void UpdateDocumentMetrics(int chars, int paragraphs, bool isEmpty)
    {
        DocumentChars = chars;
        ParagraphCount = paragraphs;
        IsDocumentEmpty = isEmpty;
        OnPropertyChanged(nameof(DocumentCountText));
        OnPropertyChanged(nameof(ParagraphText));
    }

    public void SetSaveStatus(string text, bool saved = true)
    {
        SaveStatusText = text;
        HasUnsavedChanges = !saved;
    }

    public void SetTheme(string theme)
    {
        if (theme is not ("Dark" or "Light"))
        {
            return;
        }

        if (Settings.Theme == theme)
        {
            return;
        }

        Settings.Theme = theme;
        ThemeChangedByUser?.Invoke();
    }

    public void Notify(string propertyName) => OnPropertyChanged(propertyName);

    public void RequestShowMain() => ShowMainRequested?.Invoke();

    public void OpenDataFolder() => OpenFolder(AppPaths.DataDir);

    public void OpenDraftFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.DraftDirectory);
        }
        catch
        {
            // Ignore; the explorer call below will surface a friendly failure.
        }

        OpenFolder(Settings.DraftDirectory);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Opening a folder is a convenience; never crash over it.
        }
    }

    public void ApplyTheme()
    {
        ThemeManager.Apply(Settings.Theme);
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    public void Shutdown()
    {
        Engine.Persist();
        SettingsService.Flush();
        _wordIntegration.Dispose();
        _plainTextIntegration.Dispose();
        Keyboard.Dispose();
        Engine.Dispose();
        SettingsService.Dispose();
    }

    /// <summary>Developer-only hook used by the screenshot preview harness.</summary>
    internal void SeedPreviewSession()
    {
        Engine.SeedSessionForPreview(1268, 26 * 60 + 40, 9 * 60 + 44, 3, 47.4);
        UpdateDocumentMetrics(1832, 24, false);
        SetSaveStatus($"已保存 {DateTime.Now:HH:mm}");
        RefreshDerived();
        RefreshBars();
    }

    // ------------------------------------------------------------ internals

    private void OnEngineTick()
    {
        RefreshDerived();
        RefreshBars();
        OnPropertyChanged(nameof(GoalCaption));
        OnPropertyChanged(nameof(GoalRemainingText));
        OnPropertyChanged(nameof(StreakText));
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StatsEngine.WordsToday))
        {
            OnPropertyChanged(nameof(GoalCaption));
            OnPropertyChanged(nameof(GoalRemainingText));
        }
    }

    private void OnDayChanged()
    {
        RebuildBarSources();
        RefreshDerived();
        RefreshBars();
    }

    private void RefreshDerived()
    {
        StreakDays = History.Streak(Engine.WordsToday);
        MonthWords = History.WordsInMonth(DateTime.Today, Engine.WordsToday);
        TotalWords = History.TotalWords(Engine.WordsToday);
        TodayDateText = Formatters.TodayLabel();
    }

    private void OnSettingChanged(object? sender, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(AppSettings.Theme):
                ThemeManager.Apply(Settings.Theme);
                OnPropertyChanged(nameof(Theme));
                OnPropertyChanged(nameof(IsDarkTheme));
                break;
            case nameof(AppSettings.GlobalTracking):
                ConfigureKeyboard();
                OnPropertyChanged(nameof(KeyboardWarningText));
                break;
            case nameof(AppSettings.WordTracking):
                if (!Settings.WordTracking)
                {
                    IsWordTrackingActive = false;
                }

                break;
            case nameof(AppSettings.PlainTextTracking):
                if (!Settings.PlainTextTracking)
                {
                    IsPlainTextTrackingActive = false;
                }

                break;
            case nameof(AppSettings.ShowOverlayDocumentName):
                OnPropertyChanged(nameof(ShowOverlayDocumentTitle));
                break;
            case nameof(AppSettings.DraftDirectory):
                OnPropertyChanged(nameof(DraftDirectory));
                break;
            case nameof(AppSettings.ShowOverlay):
            case nameof(AppSettings.OverlayClickThrough):
                OnPropertyChanged(propertyName);
                break;
        }
    }

    private void ConfigureKeyboard()
    {
        if (Settings.GlobalTracking)
        {
            if (!_keyboardHooked)
            {
                Keyboard.KeyPressed += OnGlobalKeyPressed;
                _keyboardHooked = true;
            }

            Keyboard.Start();
        }
        else
        {
            Keyboard.Stop();
        }
    }

    private void OnGlobalKeyPressed(WinForms.Keys key)
    {
        if (KeyboardActivityMonitor.IsModifierKey(key) ||
            KeyboardActivityMonitor.HasCommandModifier() ||
            !KeyboardActivityMonitor.IsTypingKey(key))
        {
            // Ctrl/Alt/Win shortcuts, modifiers and function keys are commands, not writing.
            return;
        }

        var estimate = Settings.ExternalKeyEstimate &&
                       !KeyboardActivityMonitor.IsForegroundOurProcess() &&
                       !(Settings.WordTracking && _wordIntegration.IsWordForeground) &&
                       !_plainTextIntegration.IsActive &&
                       !ImeComposition.IsComposing(ImeComposition.ForegroundWindow);
        var delta = estimate ? KeyboardActivityMonitor.EstimateCharacterDelta(key) : 0;
        Engine.NoteExternalActivity(delta);
    }

    private void RebuildBarSources()
    {
        _recentSource.Clear();
        _recentSource.AddRange(History.RecentDays(14));

        RecentDays.Clear();
        for (var i = 0; i < _recentSource.Count; i++)
        {
            var date = ParseDate(_recentSource[i].Date);
            RecentDays.Add(new DayBarViewModel(_recentSource[i].Date, Formatters.DateShort(date), string.Empty)
            {
                IsToday = i == _recentSource.Count - 1,
            });
        }

        RefreshBars();
    }

    private void RefreshBars()
    {
        UpdateBarSet(_recentSource, RecentDays, 118);
    }

    private void UpdateBarSet(List<DailyRecord> source, ObservableCollection<DayBarViewModel> bars, double maxHeight)
    {
        if (source.Count == 0 || bars.Count != source.Count)
        {
            return;
        }

        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        var values = new long[source.Count];
        long max = 1;
        for (var i = 0; i < source.Count; i++)
        {
            var value = source[i].Date == todayKey ? Engine.WordsToday : source[i].Words;
            values[i] = value;
            if (value > max)
            {
                max = value;
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var bar = bars[i];
            var value = values[i];
            bar.Value = value;
            bar.BarHeight = value <= 0 ? 3 : Math.Max(6, value / (double)max * maxHeight);
            var date = ParseDate(source[i].Date);
            bar.Tooltip = $"{date:M 月 d 日} · {value:N0} 字";
        }
    }

    private static DateTime ParseDate(string value)
        => DateTime.TryParse(value, out var date) ? date : DateTime.Today;
}
