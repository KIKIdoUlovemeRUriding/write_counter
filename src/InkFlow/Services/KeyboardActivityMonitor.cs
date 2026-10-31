using System.Diagnostics;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace InkFlow.Services;

/// <summary>
/// Low-level global keyboard hook. It never records what is typed — it only
/// reports that a key was pressed (and, optionally, an estimated character delta)
/// so the timer can tell writing apart from resting in any application.
/// </summary>
public sealed class KeyboardActivityMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;

    public bool IsRunning => _hook != IntPtr.Zero;

    public string? LastError { get; private set; }

    /// <summary>Raised for every key down, on the UI thread.</summary>
    public event Action<WinForms.Keys>? KeyPressed;

    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error().ToString();
            _proc = null;
            return false;
        }

        LastError = null;
        return true;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            try
            {
                KeyPressed?.Invoke((WinForms.Keys)info.VkCode);
            }
            catch
            {
                // Never let an exception escape into the OS hook chain.
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Estimated character count for a key press, used only in opt-in estimate mode.</summary>
    public static int EstimateCharacterDelta(WinForms.Keys key)
    {
        if (IsModifierDown(VK_CONTROL) || IsModifierDown(VK_MENU))
        {
            return 0;
        }

        if (key == WinForms.Keys.Back)
        {
            return -1;
        }

        var code = (int)key;
        return code switch
        {
            >= 0x30 and <= 0x39 => 1, // 0-9
            >= 0x41 and <= 0x5A => 1, // A-Z
            >= 0x60 and <= 0x69 => 1, // numpad
            0x20 => 1, // space
            0x0D => 1, // enter
            >= 0xBA and <= 0xC0 => 1, // OEM punctuation
            >= 0xDB and <= 0xDE => 1, // brackets / quotes
            _ => 0,
        };
    }

    private static bool IsModifierDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True while a Ctrl/Alt/Win shortcut modifier is held down.</summary>
    public static bool HasCommandModifier()
        => IsModifierDown(VK_CONTROL) || IsModifierDown(VK_MENU) ||
           IsModifierDown(VK_LWIN) || IsModifierDown(VK_RWIN);

    /// <summary>Modifier keys themselves never count as writing activity.</summary>
    public static bool IsModifierKey(WinForms.Keys key)
        => (int)key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>Keys that plausibly represent typing (letters, digits, punctuation, editing keys).</summary>
    public static bool IsTypingKey(WinForms.Keys key)
    {
        var code = (int)key;
        if (code is 0x08 or 0x09 or 0x0D or 0x2E or 0xE5)
        {
            return true;
        }

        return code is >= 0x20 and <= 0x5A
            or >= 0x60 and <= 0x6F
            or >= 0xBA and <= 0xDF;
    }

    public static bool IsForegroundOurProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>Checks which process owns the foreground window (e.g. WINWORD).</summary>
    public static bool IsForegroundProcess(string processName)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
