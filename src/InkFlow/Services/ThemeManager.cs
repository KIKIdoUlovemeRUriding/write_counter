using System.Windows;

namespace InkFlow.Services;

/// <summary>Swaps the active color dictionary at runtime.</summary>
public static class ThemeManager
{
    public static string Current { get; private set; } = "Dark";

    public static event EventHandler? ThemeChanged;

    public static void Apply(string theme)
    {
        if (theme is not ("Dark" or "Light"))
        {
            theme = "Dark";
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative),
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            merged[0] = dictionary;
        }

        Current = theme;

        foreach (Window window in app.Windows)
        {
            var hwnd = WindowEffects.Hwnd(window);
            if (hwnd != IntPtr.Zero)
            {
                WindowEffects.SetDarkFrame(window, theme == "Dark");
            }
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
