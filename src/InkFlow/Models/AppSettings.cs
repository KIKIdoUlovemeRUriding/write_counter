using System.Collections.Generic;
using InkFlow.Core;
using InkFlow.Services;

namespace InkFlow.Models;

/// <summary>All persisted user preferences.</summary>
public sealed class AppSettings : ObservableObject
{
    private string _theme = "Dark";
    private int _dailyGoal = 2000;
    private int _breakReminderMinutes = 25;
    private int _idleThresholdSeconds = 6;
    private int _autoPauseMinutes = 30;
    private bool _showOverlay = true;
    private bool _showOverlayDocumentName = true;
    private string _overlayManualTitle = string.Empty;
    private Dictionary<string, string> _documentTitleOverrides = new();
    private bool _overlayExpanded;
    private bool _overlayMinimized;
    private double? _overlayRestoreLeft;
    private double? _overlayRestoreTop;
    private bool _overlayLocked;
    private bool _overlayClickThrough;
    private double _overlayBackgroundOpacity = 0.96;
    private double _overlayTextOpacity = 1.0;
    private double _overlayScale = 1.0;
    private double? _overlayLeft;
    private double? _overlayTop;
    private bool _globalTracking = true;
    private bool _wordTracking = true;
    private bool _plainTextTracking = true;
    private bool _externalKeyEstimate;
    private bool _soundEnabled = true;
    private bool _closeToTray = true;
    private bool _hotkeysEnabled = true;
    private double _editorFontSize = 17.5;
    private string _editorFont = "Microsoft YaHei UI";
    private string _draftDirectory = string.Empty;
    private string _lastDocumentPath = string.Empty;
    private double? _mainLeft;
    private double? _mainTop;
    private double? _mainWidth;
    private double? _mainHeight;
    private bool _mainMaximized;
    private bool _welcomeShown;
    private bool _overlayHotkeyNoticeShown;

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public int DailyGoal
    {
        get => _dailyGoal;
        set => SetProperty(ref _dailyGoal, value);
    }

    public int BreakReminderMinutes
    {
        get => _breakReminderMinutes;
        set => SetProperty(ref _breakReminderMinutes, value);
    }

    public int IdleThresholdSeconds
    {
        get => _idleThresholdSeconds;
        set => SetProperty(ref _idleThresholdSeconds, value);
    }

    public int AutoPauseMinutes
    {
        get => _autoPauseMinutes;
        set => SetProperty(ref _autoPauseMinutes, value);
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set => SetProperty(ref _showOverlay, value);
    }

    /// <summary>Show the current document / Word article name on the floating card.</summary>
    public bool ShowOverlayDocumentName
    {
        get => _showOverlayDocumentName;
        set => SetProperty(ref _showOverlayDocumentName, value);
    }

    /// <summary>
    /// Sticky title typed by the user. While non-empty the floating card keeps
    /// this name and stops following Word / TXT / draft document titles.
    /// </summary>
    public string OverlayManualTitle
    {
        get => _overlayManualTitle;
        set => SetProperty(ref _overlayManualTitle, value ?? string.Empty);
    }

    /// <summary>Custom display names for the floating card, keyed by document identity.</summary>
    public Dictionary<string, string> DocumentTitleOverrides
    {
        get => _documentTitleOverrides;
        set => SetProperty(ref _documentTitleOverrides, value ?? new Dictionary<string, string>());
    }

    public bool OverlayExpanded
    {
        get => _overlayExpanded;
        set => SetProperty(ref _overlayExpanded, value);
    }

    /// <summary>True while the floating card is docked to a screen edge as a slim strip.</summary>
    public bool OverlayMinimized
    {
        get => _overlayMinimized;
        set => SetProperty(ref _overlayMinimized, value);
    }

    /// <summary>Window position before minimizing, used when restoring.</summary>
    public double? OverlayRestoreLeft
    {
        get => _overlayRestoreLeft;
        set => SetProperty(ref _overlayRestoreLeft, value);
    }

    public double? OverlayRestoreTop
    {
        get => _overlayRestoreTop;
        set => SetProperty(ref _overlayRestoreTop, value);
    }

    public bool OverlayLocked
    {
        get => _overlayLocked;
        set => SetProperty(ref _overlayLocked, value);
    }

    public bool OverlayClickThrough
    {
        get => _overlayClickThrough;
        set => SetProperty(ref _overlayClickThrough, value);
    }

    /// <summary>Opacity of the glass background and border only.</summary>
    public double OverlayBackgroundOpacity
    {
        get => _overlayBackgroundOpacity;
        set => SetProperty(ref _overlayBackgroundOpacity, Math.Clamp(value, 0.25, 1.0));
    }

    /// <summary>Opacity of the text, icons, charts and buttons inside the card.</summary>
    public double OverlayTextOpacity
    {
        get => _overlayTextOpacity;
        set => SetProperty(ref _overlayTextOpacity, Math.Clamp(value, 0.4, 1.0));
    }

    public double OverlayScale
    {
        get => _overlayScale;
        set => SetProperty(ref _overlayScale, Math.Clamp(value, 0.8, 1.4));
    }

    public double? OverlayLeft
    {
        get => _overlayLeft;
        set => SetProperty(ref _overlayLeft, value);
    }

    public double? OverlayTop
    {
        get => _overlayTop;
        set => SetProperty(ref _overlayTop, value);
    }

    public bool GlobalTracking
    {
        get => _globalTracking;
        set => SetProperty(ref _globalTracking, value);
    }

    /// <summary>Read the active Word document's character count (statistics only) while Word is in front.</summary>
    public bool WordTracking
    {
        get => _wordTracking;
        set => SetProperty(ref _wordTracking, value);
    }

    /// <summary>Read the active TXT editor's text through Windows UI Automation.</summary>
    public bool PlainTextTracking
    {
        get => _plainTextTracking;
        set => SetProperty(ref _plainTextTracking, value);
    }

    public bool ExternalKeyEstimate
    {
        get => _externalKeyEstimate;
        set => SetProperty(ref _externalKeyEstimate, value);
    }

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set => SetProperty(ref _soundEnabled, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool HotkeysEnabled
    {
        get => _hotkeysEnabled;
        set => SetProperty(ref _hotkeysEnabled, value);
    }

    public double EditorFontSize
    {
        get => _editorFontSize;
        set => SetProperty(ref _editorFontSize, Math.Clamp(value, 13, 28));
    }

    public string EditorFont
    {
        get => _editorFont;
        set => SetProperty(ref _editorFont, string.IsNullOrWhiteSpace(value) ? "Microsoft YaHei UI" : value);
    }

    public string DraftDirectory
    {
        get => _draftDirectory;
        set => SetProperty(ref _draftDirectory, value);
    }

    public string LastDocumentPath
    {
        get => _lastDocumentPath;
        set => SetProperty(ref _lastDocumentPath, value);
    }

    public double? MainLeft
    {
        get => _mainLeft;
        set => SetProperty(ref _mainLeft, value);
    }

    public double? MainTop
    {
        get => _mainTop;
        set => SetProperty(ref _mainTop, value);
    }

    public double? MainWidth
    {
        get => _mainWidth;
        set => SetProperty(ref _mainWidth, value is { } width ? Math.Clamp(width, 980, 4000) : null);
    }

    public double? MainHeight
    {
        get => _mainHeight;
        set => SetProperty(ref _mainHeight, value is { } height ? Math.Clamp(height, 620, 3000) : null);
    }

    public bool MainMaximized
    {
        get => _mainMaximized;
        set => SetProperty(ref _mainMaximized, value);
    }

    public bool WelcomeShown
    {
        get => _welcomeShown;
        set => SetProperty(ref _welcomeShown, value);
    }

    public bool OverlayHotkeyNoticeShown
    {
        get => _overlayHotkeyNoticeShown;
        set => SetProperty(ref _overlayHotkeyNoticeShown, value);
    }

    /// <summary>Fixes up values loaded from an older or hand-edited settings file.</summary>
    public void Normalize()
    {
        if (Theme is not ("Dark" or "Light"))
        {
            Theme = "Dark";
        }

        DailyGoal = Math.Clamp(DailyGoal, 0, 200_000);
        BreakReminderMinutes = Math.Clamp(BreakReminderMinutes, 0, 180);
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 2, 60);
        AutoPauseMinutes = Math.Clamp(AutoPauseMinutes, 0, 240);
        OverlayBackgroundOpacity = OverlayBackgroundOpacity;
        OverlayTextOpacity = OverlayTextOpacity;
        OverlayScale = OverlayScale;
        EditorFontSize = EditorFontSize;

        if (string.IsNullOrWhiteSpace(DraftDirectory))
        {
            DraftDirectory = AppPaths.DefaultDraftDirectory;
        }

        if (string.IsNullOrWhiteSpace(EditorFont))
        {
            EditorFont = "Microsoft YaHei UI";
        }
    }
}
