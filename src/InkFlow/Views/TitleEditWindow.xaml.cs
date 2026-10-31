using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace InkFlow.Views;

public partial class TitleEditWindow : Window
{
    private const int WhMouseLl = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;

    private bool _closing;
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;

    public TitleEditWindow(string initialTitle)
    {
        InitializeComponent();
        TitleBox.Text = initialTitle;

        Loaded += (_, _) =>
        {
            Activate();
            TitleBox.Focus();
            TitleBox.SelectAll();
            InstallMouseHook();
        };

        Closed += (_, _) => UninstallMouseHook();

        // Also handles clicks on ordinary windows. The low-level hook below covers
        // the no-activate floating card, which never takes focus from this dialog.
        Deactivated += (_, _) => Cancel();
    }

    public string Result { get; private set; } = string.Empty;

    public bool Saved { get; private set; }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void Commit()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Result = TitleBox.Text.Trim();
        Saved = true;
        DialogResult = true;
    }

    private void Cancel()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Saved = false;
        DialogResult = false;
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing)
        {
            var message = wParam.ToInt32();
            if (message is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (!IsPointInsideEditor(info.Point.X, info.Point.Y))
                {
                    Dispatcher.BeginInvoke(new Action(Cancel));
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideEditor(int x, int y)
    {
        if (!IsLoaded)
        {
            return true;
        }

        var topLeft = EditorCard.PointToScreen(new Point(0, 0));
        var bottomRight = EditorCard.PointToScreen(new Point(EditorCard.ActualWidth, EditorCard.ActualHeight));
        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
