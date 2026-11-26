using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BAM.Desktop.Controls;
using BAM.Desktop.Data;
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
    private readonly LiveDesktopData? _live;
    private readonly Func<Task> _prepareExit;
    private HwndSource? _windowSource;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _exitPrepared;
    private WindowState _restoreState = WindowState.Normal;
#if BAM_DEMO || BAM_VERIFICATION
    private string? _captureDirectory;
    private DemoCapture? _capture;

    internal void EnableCapture(string directory) => _captureDirectory = directory;
#endif

    internal MainWindow(ShellViewModel shell, SettingsViewModel settings, ThemeController theme,
        LiveDesktopData? live, Func<Task> prepareExit)
    {
        _shell = shell;
        _settings = settings;
        _theme = theme;
        _live = live;
        _prepareExit = prepareExit;
        InitializeComponent();
        DataContext = _shell;
        IsVisibleChanged += (_, _) => ConfigureMonitoring();
        _settings.PropertyChanged += SettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(ShowKeyboardSystemMenu);
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
#if BAM_DEMO || BAM_VERIFICATION
        if (_captureDirectory is not null) _capture = new DemoCapture(this, Shell, _captureDirectory);
#endif
    }

    private IntPtr ShowKeyboardSystemMenu(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int systemCommand = 0x0112;
        const int keyboardMenu = 0xF100;
        const int space = 0x20;
        if (message != systemCommand || (wParam.ToInt64() & 0xFFF0) != keyboardMenu || lParam.ToInt64() != space ||
            !IsVisible || WindowState == WindowState.Minimized || _windowSource?.CompositionTarget is not { } target)
            return IntPtr.Zero;

        handled = true;
        var anchor = Shell.PointToScreen(new Point(0, Shell.RowDefinitions[0].ActualHeight));
        SystemCommands.ShowSystemMenu(this, target.TransformFromDevice.Transform(anchor));
        return IntPtr.Zero;
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
        ConfigureMonitoring();
    }

    private void OpenOverview()
    {
        _shell.OpenOverview();
        Restore();
    }

    internal void AllowSessionEnd() => _exitPrepared = true;

    private void ExitFromTray() => BeginExit();

    private async void BeginExit()
    {
        if (_exiting) return;
        _exiting = true;
        await Task.Yield();
        await _prepareExit();
        _exitPrepared = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _exitPrepared) return;
        e.Cancel = true;
        if (!_exiting && _settings.CloseToTray && _tray is not null)
        {
            Hide();
            if (_settings.MarkTrayIntroSeen()) _tray.ShowIntro();
        }
        else BeginExit();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SettingsViewModel.CollectResources)) return;
        ConfigureMonitoring();
        _tray?.SetStatus(_shell.MonitoringStatus);
    }

    private void ConfigureMonitoring() => _live?.Configure(_settings.CollectResources,
        TimeSpan.FromSeconds(!IsVisible ? 10 : WindowState == WindowState.Minimized ? 5 : 1));

    protected override void OnClosed(EventArgs e)
    {
        _settings.PropertyChanged -= SettingsChanged;
        _windowSource?.RemoveHook(ShowKeyboardSystemMenu);
        _windowSource = null;
#if BAM_DEMO || BAM_VERIFICATION
        _capture?.Dispose();
#endif
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }
}
