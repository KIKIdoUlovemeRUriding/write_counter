using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace InkFlow.Services;

/// <summary>Registers system-wide hotkeys on the main window's message loop.</summary>
public sealed class HotKeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly HashSet<int> _registered = new();
    private bool _hookAttached;

    public HotKeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source is not null)
        {
            _source.AddHook(WndProc);
            _hookAttached = true;
        }
    }

    public List<string> Failures { get; } = new();

    public bool Register(int id, ModifierKeys modifiers, Key key, string label, Action action)
    {
        var modifiersValue = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiersValue |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiersValue |= MOD_ALT;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiersValue |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiersValue |= MOD_WIN;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_hwnd, id, modifiersValue, vk))
        {
            Failures.Add(label);
            return false;
        }

        _registered.Add(id);
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            UnregisterHotKey(_hwnd, id);
        }

        _registered.Clear();
        _actions.Clear();
        Failures.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                handled = true;
                action();
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_hookAttached && _source is not null)
        {
            _source.RemoveHook(WndProc);
            _hookAttached = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
