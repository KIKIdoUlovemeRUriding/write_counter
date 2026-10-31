using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using InkFlow.Models;

namespace InkFlow.Services;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };
}

public static class FileUtil
{
    /// <summary>Writes text atomically so a crash never leaves a half-written file.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}

/// <summary>Loads and persists <see cref="AppSettings"/> with a debounce for slider changes.</summary>
public sealed class SettingsService : IDisposable
{
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private bool _loaded;

    public SettingsService()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
            }
            else
            {
                Settings = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Settings = new AppSettings();
            LastError = ex.Message;
        }

        Settings.Normalize();

        if (string.IsNullOrWhiteSpace(Settings.DraftDirectory))
        {
            Settings.DraftDirectory = AppPaths.DefaultDraftDirectory;
        }

        Settings.PropertyChanged += OnSettingsChanged;

        _saveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        _loaded = true;
    }

    public AppSettings Settings { get; }

    /// <summary>Raised after any setting changes (after the timer-based save is scheduled).</summary>
    public event EventHandler<string>? SettingChanged;

    public string? LastError { get; private set; }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        SettingChanged?.Invoke(this, e.PropertyName ?? string.Empty);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var json = JsonSerializer.Serialize(Settings, JsonDefaults.Options);
            FileUtil.WriteAllTextAtomic(AppPaths.SettingsFile, json);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
        }

        Save();
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        Settings.PropertyChanged -= OnSettingsChanged;
    }
}
