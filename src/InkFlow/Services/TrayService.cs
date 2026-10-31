using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using InkFlow.Models;
using WinForms = System.Windows.Forms;

namespace InkFlow.Services;

/// <summary>System tray icon and menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppState _state;
    private readonly Action _showMain;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly DispatcherTimer _tooltipTimer;

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _overlayItem;
    private WinForms.ToolStripMenuItem? _lockItem;
    private WinForms.ToolStripMenuItem? _clickThroughItem;
    private WinForms.ToolStripMenuItem? _pauseItem;
    private Icon? _icon;
    private int _ticksSinceTooltip;

    public TrayService(AppState state, Action showMain, Action showSettings, Action exit)
    {
        _state = state;
        _showMain = showMain;
        _showSettings = showSettings;
        _exit = exit;

        _tooltipTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();

        _state.Settings.PropertyChanged += (_, _) => UpdateMenuState();
        _state.Engine.Tick += (_, _) =>
        {
            _ticksSinceTooltip++;
            if (_ticksSinceTooltip >= 5)
            {
                _ticksSinceTooltip = 0;
                UpdateTooltip();
                UpdateMenuState();
            }
        };
    }

    public void Initialize()
    {
        _icon = LoadIcon();
        _menu = BuildMenu();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "墨流 · 码字助手",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseDoubleClick += (_, _) => _showMain();

        UpdateMenuState();
        UpdateTooltip();
        _tooltipTimer.Start();
    }

    private Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative));
            if (resource?.Stream is { } stream)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            // Fall back to the executable icon.
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Ignore and use the stock icon.
        }

        return SystemIcons.Application;
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            BackColor = Color.FromArgb(16, 21, 31),
            ForeColor = Color.FromArgb(228, 235, 246),
            Padding = new WinForms.Padding(5),
        };

        var open = CreateItem("打开墨流", () => _showMain(), bold: true);
        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _overlayItem = CreateItem("显示浮窗", () => _state.Settings.ShowOverlay = !_state.Settings.ShowOverlay, checkable: true);
        _lockItem = CreateItem("锁定浮窗", () => _state.Settings.OverlayLocked = !_state.Settings.OverlayLocked, checkable: true);
        _clickThroughItem = CreateItem("鼠标穿透", () => _state.Settings.OverlayClickThrough = !_state.Settings.OverlayClickThrough, checkable: true);
        menu.Items.Add(_overlayItem);
        menu.Items.Add(_lockItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _pauseItem = CreateItem("暂停计时", () => _state.Engine.TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(CreateItem("休息一下", () => _state.Engine.BeginBreak()));
        menu.Items.Add(CreateItem("开始新的一节", () => _state.Engine.StartNewSession()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateItem("设置", () => _showSettings()));
        menu.Items.Add(CreateItem("退出墨流", () => _exit()));

        return menu;
    }

    private WinForms.ToolStripMenuItem CreateItem(string text, Action action, bool checkable = false, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            ForeColor = Color.FromArgb(228, 235, 246),
            Padding = new WinForms.Padding(6, 4, 18, 4),
            CheckOnClick = false,
            Font = new Font("Microsoft YaHei UI", 9f, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular),
        };

        item.Click += (_, _) => action();
        return item;
    }

    private void UpdateMenuState()
    {
        if (_menu is null)
        {
            return;
        }

        var settings = _state.Settings;
        if (_overlayItem is not null)
        {
            _overlayItem.Checked = settings.ShowOverlay;
        }

        if (_lockItem is not null)
        {
            _lockItem.Checked = settings.OverlayLocked;
        }

        if (_clickThroughItem is not null)
        {
            _clickThroughItem.Checked = settings.OverlayClickThrough;
        }

        if (_pauseItem is not null)
        {
            _pauseItem.Text = _state.Engine.IsPaused || !_state.Engine.IsStarted ? "继续计时" : "暂停计时";
        }
    }

    private void UpdateTooltip()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var engine = _state.Engine;
        var status = engine.IsPaused ? "已暂停" : engine.IsStarted ? engine.StatusText : "等待开始";
        _notifyIcon.Text = $"墨流 · {status} · 今日 {engine.WordsToday:N0} 字";
    }

    public void Dispose()
    {
        _tooltipTimer.Stop();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu?.Dispose();
        _menu = null;

        if (_icon is not null)
        {
            _icon.Dispose();
            _icon = null;
        }
    }

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Color BackgroundColor = Color.FromArgb(16, 21, 31);
        private static readonly Color BorderColor = Color.FromArgb(48, 58, 76);
        private static readonly Color HoverColor = Color.FromArgb(35, 43, 58);
        private static readonly Color PressedColor = Color.FromArgb(28, 35, 48);
        private static readonly Color CheckColor = Color.FromArgb(47, 41, 92);

        public override Color ToolStripDropDownBackground => BackgroundColor;

        public override Color ImageMarginGradientBegin => BackgroundColor;

        public override Color ImageMarginGradientMiddle => BackgroundColor;

        public override Color ImageMarginGradientEnd => BackgroundColor;

        public override Color MenuBorder => BorderColor;

        public override Color MenuItemBorder => Color.FromArgb(72, 64, 140);

        public override Color MenuItemSelected => HoverColor;

        public override Color MenuItemSelectedGradientBegin => HoverColor;

        public override Color MenuItemSelectedGradientEnd => HoverColor;

        public override Color MenuItemPressedGradientBegin => PressedColor;

        public override Color MenuItemPressedGradientEnd => PressedColor;

        public override Color SeparatorDark => Color.FromArgb(38, 47, 62);

        public override Color SeparatorLight => Color.FromArgb(16, 21, 31);

        public override Color CheckBackground => CheckColor;

        public override Color CheckSelectedBackground => Color.FromArgb(62, 53, 120);

        public override Color CheckPressedBackground => Color.FromArgb(62, 53, 120);
    }
}
