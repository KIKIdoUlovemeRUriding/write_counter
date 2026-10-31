using System.IO;

namespace InkFlow.Services;

/// <summary>Central definition of where InkFlow keeps settings, stats and drafts.</summary>
public static class AppPaths
{
    private const string AppFolderName = "InkFlow";

    /// <summary>
    /// Data folder. Can be overridden with the INKFLOW_DATA_DIR environment variable
    /// (used by the UI preview harness and portable installs).
    /// </summary>
    public static string DataDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("INKFLOW_DATA_DIR");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName)
                : Path.GetFullPath(custom);
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string HistoryFile => Path.Combine(DataDir, "history.json");

    public static string DefaultDraftDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "墨流");

    public static string DefaultDraftFile => Path.Combine(DefaultDraftDirectory, "我的草稿.md");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDir);
}
