using System.Runtime.InteropServices;

namespace InkFlow.Services;

/// <summary>
/// Detects an active IME composition (the pinyin/candidate string that has not
/// been committed yet). The editor and the Word tracker must ignore counts while
/// a composition is in progress, otherwise pinyin letters would be counted as text.
/// </summary>
internal static class ImeComposition
{
    private const int GCS_COMPREADSTR = 0x0001;
    private const int GCS_COMPSTR = 0x0008;

    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static bool IsComposing(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return QueryLength(context, GCS_COMPSTR) > 0 || QueryLength(context, GCS_COMPREADSTR) > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    private static int QueryLength(IntPtr context, int index)
    {
        var length = ImmGetCompositionStringW(context, index, null, 0);
        return length > 0 ? length : 0;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[]? lpBuf, int dwBufLen);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
