using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BAM.Desktop.Controls;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace BAM.Desktop;

public partial class MainWindow : FluentWindow
{
    public static readonly DependencyProperty LabelsVisibilityProperty = DependencyProperty.Register(
        nameof(LabelsVisibility), typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public Visibility LabelsVisibility => (Visibility)GetValue(LabelsVisibilityProperty);
    public bool IsCompact => (bool)GetValue(IsCompactProperty);

    private readonly ShellViewModel _shell;
    private readonly SettingsViewModel _settings;
    private readonly ThemeController _theme;
    private TrayIcon? _tray;
    private bool _exiting;
    private WindowState _restoreState = WindowState.Normal;
#if BAM_DEMO
    private string? _captureDirectory;
    private DemoCapture? _capture;

    internal void EnableCapture(string directory) => _captureDirectory = directory;
#endif

    internal MainWindow(ShellViewModel shell, SettingsViewModel settings, ThemeController theme)
    {
        _shell = shell;
        _settings = settings;
        _theme = theme;
        InitializeComponent();
        DataContext = _shell;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _theme.Attach(this);

        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = area.Width / dpi.DpiScaleX;
        var height = area.Height / dpi.DpiScaleY;
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = Math.Min(Width, width);
        Height = Math.Min(Height, height);
        _tray = new TrayIcon(Restore, OpenOverview, ExitFromTray, _shell.MonitoringStatus);
#if BAM_DEMO
        if (_captureDirectory is not null) _capture = new DemoCapture(this, Shell, _captureDirectory);
#endif
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (RailColumn is null || !sizeInfo.WidthChanged) return;
        var collapsed = ActualWidth < (double)FindResource("Bam.RailThreshold");
        RailColumn.Width = new GridLength((double)FindResource(collapsed ? "Bam.RailCollapsedWidth" : "Bam.RailWidth"));
        SetValue(LabelsVisibilityProperty, collapsed ? Visibility.Collapsed : Visibility.Visible);
        SetValue(IsCompactProperty, ActualWidth < (double)FindResource("Bam.CompactThreshold"));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement page) Motion.ShowPage(page);
    }

    internal void Restore()
    {
        if (_exiting) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        base.OnStateChanged(e);
    }

    private void OpenOverview()
    {
        _shell.OpenOverview();
        Restore();
    }

    internal void AllowClose() => _exiting = true;

    private void ExitFromTray()
    {
        AllowClose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _exiting || !_settings.CloseToTray || _tray is null) return;
        e.Cancel = true;
        Hide();
        if (_settings.MarkTrayIntroSeen()) _tray.ShowIntro();
    }

    protected override void OnClosed(EventArgs e)
    {
#if BAM_DEMO
        _capture?.Dispose();
#endif
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }
}
