using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using InkFlow.Core;

namespace InkFlow.Services;

/// <summary>
/// Counts text written in plain-text editors (Notepad, Notepad++, WordPad,
/// Sublime Text and other apps that expose their document through Windows
/// UI Automation). Only the focused editor's text is read, locally, to compute
/// the character delta. Nothing is uploaded.
/// </summary>
public sealed class PlainTextIntegrationService : IDisposable
{
    private static readonly HashSet<string> SupportedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad",
        "notepad++",
        "wordpad",
        "notepad3",
        "sublime_text",
        "gvim",
        "emacs",
        "textpad",
        "ultraedit",
    };

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "inkflow",
        "winword",
        "code",
        "code - insiders",
        "chrome",
        "msedge",
        "firefox",
        "opera",
        "brave",
        "vivaldi",
    };

    private static readonly string[] WindowTitleSuffixes =
    {
        " - 记事本",
        " - Notepad",
        " - Notepad++",
        " - WordPad",
        " - 写字板",
        " - Sublime Text",
        " - Notepad3",
    };

    private const int MaxReasonableDelta = 5000;

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;

    private volatile bool _stop;
    private volatile bool _started;
    private volatile bool _active;

    private string? _lastKey;
    private string? _lastTitle;
    private string? _lastBadge;
    private int _lastCount;
    private bool _hasBaseline;

    public PlainTextIntegrationService(SettingsService settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "InkFlow.PlainTextTracker",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public event Action<long>? WordDelta;

    public event Action<bool>? ActiveChanged;

    /// <summary>Document title and a short source badge (TXT, N++, …).</summary>
    public event Action<string, string>? DocumentChanged;

    public bool IsActive => _active;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _thread.Start();
    }

    private void Worker()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            while (!_stop)
            {
                try
                {
                    if (!_settings.Settings.PlainTextTracking)
                    {
                        Deactivate();
                        ResetBaseline();
                        Thread.Sleep(1500);
                        continue;
                    }

                    var hwnd = GetForegroundWindow();
                    if (hwnd == IntPtr.Zero)
                    {
                        Deactivate();
                        ResetBaseline();
                        Thread.Sleep(1200);
                        continue;
                    }

                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == Environment.ProcessId)
                    {
                        Deactivate();
                        ResetBaseline();
                        Thread.Sleep(1200);
                        continue;
                    }

                    string processName;
                    using (var process = Process.GetProcessById((int)pid))
                    {
                        processName = process.ProcessName;
                    }

                    var windowTitle = GetWindowTitle(hwnd);
                    if (!IsSupported(processName, windowTitle))
                    {
                        Deactivate();
                        ResetBaseline();
                        Thread.Sleep(1200);
                        continue;
                    }

                    if (ImeComposition.IsComposing(hwnd))
                    {
                        // Do not count the pinyin/candidate string before it is committed.
                        Thread.Sleep(300);
                        continue;
                    }

                    var snapshot = ReadEditor(hwnd, (int)pid, processName);
                    if (snapshot is null)
                    {
                        Deactivate();
                        ResetBaseline();
                        Thread.Sleep(1200);
                        continue;
                    }

                    Activate();
                    PublishDocument(snapshot.Title, snapshot.Badge);

                    if (!_hasBaseline ||
                        !string.Equals(snapshot.Key, _lastKey, StringComparison.Ordinal))
                    {
                        _lastKey = snapshot.Key;
                        _lastCount = snapshot.Count;
                        _hasBaseline = true;
                    }
                    else
                    {
                        var delta = snapshot.Count - _lastCount;
                        if (Math.Abs(delta) > MaxReasonableDelta)
                        {
                            // A new file opened in the same control, or a huge replace.
                            _lastCount = snapshot.Count;
                        }
                        else if (delta != 0)
                        {
                            _lastCount = snapshot.Count;
                            var value = (long)delta;
                            _dispatcher.BeginInvoke(new Action(() => WordDelta?.Invoke(value)));
                        }
                    }
                }
                catch (Exception)
                {
                    Deactivate();
                    ResetBaseline();
                }

                Thread.Sleep(1200);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static bool IsSupported(string processName, string windowTitle)
    {
        if (ExcludedProcesses.Contains(processName))
        {
            return false;
        }

        if (SupportedProcesses.Contains(processName))
        {
            return true;
        }

        // Generic TXT fallback for editors that expose a standard document
        // through UI Automation, without touching browsers or VS Code.
        return windowTitle.Contains(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static TextSnapshot? ReadEditor(IntPtr hwnd, int pid, string processName)
    {
        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var focusedText = TryReadFocused(pid);
        if (focusedText is not null)
        {
            return BuildSnapshot(hwnd, pid, processName, focusedText.Value.Element, focusedText.Value.Text);
        }

        var typeCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var patternCondition = new OrCondition(
            new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
        var condition = new AndCondition(typeCondition, patternCondition);

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(TreeScope.Descendants, condition);
        }
        catch (Exception)
        {
            return null;
        }

        AutomationElement? bestElement = null;
        string? bestText = null;
        var bestIsDocument = false;
        var bestArea = 0.0;

        for (var i = 0; i < elements.Count && i < 24; i++)
        {
            var element = elements[i];
            var text = TryReadText(element);
            if (text is null)
            {
                continue;
            }

            var rect = element.Current.BoundingRectangle;
            if (rect.Width < 80 || rect.Height < 20)
            {
                continue;
            }

            var isDocument = element.Current.ControlType == ControlType.Document;
            var area = rect.Width * rect.Height;
            if (bestText is null ||
                text.Length > bestText.Length ||
                (text.Length == bestText.Length && isDocument && !bestIsDocument) ||
                (text.Length == bestText.Length && isDocument == bestIsDocument && area > bestArea))
            {
                bestElement = element;
                bestText = text;
                bestIsDocument = isDocument;
                bestArea = area;
            }
        }

        return bestElement is null || bestText is null
            ? null
            : BuildSnapshot(hwnd, pid, processName, bestElement, bestText);
    }

    private static (AutomationElement Element, string Text)? TryReadFocused(int pid)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != pid)
            {
                return null;
            }

            var type = focused.Current.ControlType;
            if (type != ControlType.Document && type != ControlType.Edit)
            {
                return null;
            }

            var text = TryReadText(focused);
            return text is null ? null : (focused, text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadText(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword)
            {
                return null;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                var text = ((TextPattern)textPattern).DocumentRange.GetText(-1);
                if (text is not null)
                {
                    return text;
                }
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value;
            }
        }
        catch (Exception)
        {
            // The window closed or the provider rejected the request.
        }

        return null;
    }

    private static TextSnapshot BuildSnapshot(
        IntPtr hwnd,
        int pid,
        string processName,
        AutomationElement element,
        string text)
    {
        var title = GetDisplayTitle(hwnd, processName);
        var badge = GetBadge(processName);
        int[] runtimeId;
        try
        {
            runtimeId = element.GetRuntimeId();
        }
        catch (Exception)
        {
            runtimeId = Array.Empty<int>();
        }

        var key = $"{pid}|{title}|{string.Join(".", runtimeId)}";
        var count = TextMetrics.CountCharacters(text);
        return new TextSnapshot(key, count, title, badge);
    }

    private static string GetDisplayTitle(IntPtr hwnd, string processName)
    {
        var title = GetWindowTitle(hwnd).Trim();
        title = title.TrimStart('*', '●', '•').Trim();

        foreach (var suffix in WindowTitleSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length].Trim();
                break;
            }
        }

        var extensionIndex = title.LastIndexOf(".txt", StringComparison.OrdinalIgnoreCase);
        if (extensionIndex >= 0)
        {
            var end = extensionIndex + 4;
            var slash = Math.Max(title.LastIndexOf('\\', end - 1), title.LastIndexOf('/', end - 1));
            var fileName = title.Substring(slash + 1, end - slash - 1);
            var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(withoutExtension))
            {
                return withoutExtension;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return GetBadge(processName);
    }

    private static string GetBadge(string processName)
    {
        return processName.ToLowerInvariant() switch
        {
            "notepad" => "TXT",
            "notepad++" => "N++",
            "wordpad" => "WordPad",
            "sublime_text" => "Sublime",
            "notepad3" => "TXT",
            _ => "TXT",
        };
    }

    private void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        try
        {
            _dispatcher.BeginInvoke(new Action(() => ActiveChanged?.Invoke(true)));
        }
        catch
        {
            // App is shutting down.
        }
    }

    private void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        try
        {
            _dispatcher.BeginInvoke(new Action(() => ActiveChanged?.Invoke(false)));
        }
        catch
        {
            // App is shutting down.
        }
    }

    private void PublishDocument(string title, string badge)
    {
        if (string.Equals(title, _lastTitle, StringComparison.Ordinal) &&
            string.Equals(badge, _lastBadge, StringComparison.Ordinal))
        {
            return;
        }

        _lastTitle = title;
        _lastBadge = badge;
        try
        {
            _dispatcher.BeginInvoke(new Action(() => DocumentChanged?.Invoke(title, badge)));
        }
        catch
        {
            // App is shutting down.
        }
    }

    private void ResetBaseline()
    {
        _lastKey = null;
        _lastTitle = null;
        _lastBadge = null;
        _hasBaseline = false;
    }

    public void Dispose()
    {
        _stop = true;
        WordDelta = null;
        ActiveChanged = null;
        DocumentChanged = null;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private sealed record TextSnapshot(string Key, int Count, string Title, string Badge);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
