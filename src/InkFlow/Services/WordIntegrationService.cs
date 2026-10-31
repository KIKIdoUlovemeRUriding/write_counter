using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace InkFlow.Services;

/// <summary>
/// Reads the character count of the active Microsoft Word document when Word is
/// in the foreground, so writing done in Word can feed the same counters as the
/// built-in editor. Only Word's statistics are read — never the document text.
/// </summary>
public sealed class WordIntegrationService : IDisposable
{
    private static readonly Guid WordApplicationClsid = new("000209FF-0000-0000-C000-000000000046");

    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;

    private volatile bool _stop;
    private volatile bool _started;
    private volatile bool _wordForeground;

    private string? _lastDocumentKey;
    private string? _lastDocumentTitle;
    private int _lastCount;
    private bool _hasBaseline;

    public WordIntegrationService(SettingsService settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "InkFlow.WordTracker",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Positive or negative character delta read from the active Word document.</summary>
    public event Action<long>? WordDelta;

    /// <summary>Raised when Microsoft Word gains or loses the foreground.</summary>
    public event Action<bool>? ForegroundChanged;

    /// <summary>Raised with the active Word document's display name.</summary>
    public event Action<string>? DocumentChanged;

    public bool IsWordForeground => _wordForeground;

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
        while (!_stop)
        {
            try
            {
                if (!_settings.Settings.WordTracking)
                {
                    PublishForeground(false);
                    ResetBaseline();
                    Thread.Sleep(1500);
                    continue;
                }

                if (!KeyboardActivityMonitor.IsForegroundProcess("WINWORD"))
                {
                    PublishForeground(false);
                    ResetBaseline();
                    Thread.Sleep(1000);
                    continue;
                }

                PublishForeground(true);

                if (ImeComposition.IsComposing(ImeComposition.ForegroundWindow))
                {
                    // Word may expose the IME's temporary string in its statistics;
                    // wait for the character to be committed before comparing counts.
                    Thread.Sleep(300);
                    continue;
                }

                var stats = ReadWordStats();
                if (stats is { } wordStats)
                {
                    if (!string.Equals(wordStats.Title, _lastDocumentTitle, StringComparison.Ordinal))
                    {
                        _lastDocumentTitle = wordStats.Title;
                        if (!string.IsNullOrWhiteSpace(wordStats.Title))
                        {
                            var title = wordStats.Title;
                            _dispatcher.BeginInvoke(new Action(() => DocumentChanged?.Invoke(title)));
                        }
                    }

                    if (!_hasBaseline || !string.Equals(wordStats.Key, _lastDocumentKey, StringComparison.Ordinal))
                    {
                        _lastDocumentKey = wordStats.Key;
                        _lastCount = wordStats.Count;
                        _hasBaseline = true;
                    }
                    else
                    {
                        var delta = wordStats.Count - _lastCount;
                        if (delta != 0)
                        {
                            _lastCount = wordStats.Count;
                            var value = (long)delta;
                            _dispatcher.BeginInvoke(new Action(() => WordDelta?.Invoke(value)));
                        }
                    }
                }
                else
                {
                    // Word may be busy with a dialog: never compare against a stale count.
                    ResetBaseline();
                }
            }
            catch (Exception)
            {
                ResetBaseline();
            }

            Thread.Sleep(1300);
        }
    }

    private void PublishForeground(bool active)
    {
        if (_wordForeground == active)
        {
            return;
        }

        _wordForeground = active;
        try
        {
            _dispatcher.BeginInvoke(new Action(() => ForegroundChanged?.Invoke(active)));
        }
        catch
        {
            // The app is shutting down.
        }
    }

    private void ResetBaseline()
    {
        _lastDocumentKey = null;
        _hasBaseline = false;
    }

    private static WordStats? ReadWordStats()
    {
        object? application = null;
        object? document = null;

        try
        {
            var clsid = WordApplicationClsid;
            GetActiveObject(ref clsid, IntPtr.Zero, out var wordObject);
            application = wordObject;
            if (application is null)
            {
                return null;
            }

            dynamic word = application;
            document = word.ActiveDocument;
            if (document is null)
            {
                return null;
            }

            dynamic doc = document;
            var key = Convert.ToString(doc.FullName) ?? string.Empty;
            var rawName = Convert.ToString(doc.Name) ?? string.Empty;
            var title = Path.GetFileNameWithoutExtension(rawName);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = rawName;
            }

            var count = Convert.ToInt32(doc.ComputeStatistics(3)); // wdStatisticCharacters
            return new WordStats(key, count, title);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (document is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(document);
                }
                catch
                {
                    // COM object already gone.
                }
            }

            if (application is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(application);
                }
                catch
                {
                    // COM object already gone.
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        WordDelta = null;
        ForegroundChanged = null;
        DocumentChanged = null;
    }

    private readonly record struct WordStats(string Key, int Count, string Title);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
