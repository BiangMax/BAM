using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;

namespace BAM.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notification;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Action _restore;
    private readonly Action _overview;
    private readonly Action _exit;

    public TrayIcon(Action restore, Action overview, Action exit, string status)
    {
        _restore = restore;
        _overview = overview;
        _exit = exit;
        var resource = Application.GetResourceStream(new Uri("Assets/Bam.ico", UriKind.Relative));
        using var stream = resource.Stream;
        using var icon = new Icon(stream, 32, 32);
        _icon = (Icon)icon.Clone();
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += ThemeMenu;
        _menu.Items.Add("打开总览", null, OpenOverview);
        _menu.Items.Add(status).Enabled = false;
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出 BAM", null, Exit);
        _notification = new Forms.NotifyIcon { Text = $"BAM · {status}", Icon = _icon, ContextMenuStrip = _menu };
        _notification.DoubleClick += Restore;
        _notification.Visible = true;
    }

    public void ShowIntro()
    {
        _notification.ShowBalloonTip(4000, "BAM 已收起到系统托盘",
            "双击托盘图标恢复窗口。要关闭程序，请在托盘菜单选择“退出 BAM”。", Forms.ToolTipIcon.Info);
    }

    public void SetStatus(string status)
    {
        _menu.Items[1].Text = status;
        _notification.Text = $"BAM · {status}";
    }

    private void Restore(object? sender, EventArgs args) => _restore();
    private void OpenOverview(object? sender, EventArgs args) => _overview();
    private void Exit(object? sender, EventArgs args) => _exit();

    private void ThemeMenu(object? sender, CancelEventArgs args)
    {
        if (SystemParameters.HighContrast)
        {
            _menu.Renderer = new Forms.ToolStripSystemRenderer();
            _menu.ForeColor = System.Drawing.SystemColors.MenuText;
            return;
        }
        var surface = ThemeColor("Bam.Surface");
        var hover = ThemeColor("Bam.SurfaceHover");
        var border = ThemeColor("Bam.Border");
        _menu.ForeColor = ThemeColor("Bam.TextPrimary");
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColors(surface, hover, border));
    }

    private static Color ThemeColor(string key)
    {
        var color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private sealed class MenuColors(Color surface, Color hover, Color border) : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => surface;
        public override Color MenuItemSelected => hover;
        public override Color MenuItemSelectedGradientBegin => hover;
        public override Color MenuItemSelectedGradientEnd => hover;
        public override Color MenuBorder => border;
        public override Color MenuItemBorder => hover;
        public override Color SeparatorDark => border;
        public override Color SeparatorLight => border;
    }

    public void Dispose()
    {
        _notification.Visible = false;
        _notification.DoubleClick -= Restore;
        _notification.Dispose();
        _menu.Opening -= ThemeMenu;
        _menu.Dispose();
        _icon.Dispose();
    }
}
