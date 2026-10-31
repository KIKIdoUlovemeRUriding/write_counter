namespace InkFlow.Core;

public static class TextMetrics
{
    /// <summary>Every non-whitespace character counts, punctuation included — the usual 码字 convention.</summary>
    public static int CountCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c != '\uFEFF')
            {
                count++;
            }
        }

        return count;
    }

    public static int CountParagraphs(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var hasContent = false;
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                if (hasContent)
                {
                    count++;
                }

                hasContent = false;
            }
            else if (!char.IsWhiteSpace(c))
            {
                hasContent = true;
            }
        }

        if (hasContent)
        {
            count++;
        }

        return count;
    }

    public static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string ToWindowsNewlines(string text)
        => NormalizeNewlines(text).Replace("\n", "\r\n");
}
