using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BAM.Desktop.ViewModels;

namespace BAM.Desktop.Views;

public partial class DashboardView : UserControl
{
    private readonly DispatcherTimer _timer;
    private Window? _owner;

    public DashboardView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Sample;
        Loaded += Connect;
        Unloaded += Disconnect;
        IsVisibleChanged += (_, _) => UpdateSampling();
    }

    private void Connect(object sender, RoutedEventArgs args)
    {
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.StateChanged += OnWindowStateChanged;
        UpdateSampling();
    }

    private void Disconnect(object sender, RoutedEventArgs args)
    {
        if (_owner is not null) _owner.StateChanged -= OnWindowStateChanged;
        _owner = null;
        _timer.Stop();
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateSampling();

    private void UpdateSampling()
    {
        if (!IsLoaded || !IsVisible || _owner is null || _owner.WindowState == WindowState.Minimized
            || DataContext is not DashboardViewModel { IsDemo: true, HasResourceSource: true })
        {
            _timer.Stop();
            return;
        }
        if (_timer.IsEnabled) return;
        ((DashboardViewModel)DataContext).Refresh(DateTimeOffset.UtcNow, resume: true);
        _timer.Start();
    }

    private void Sample(object? sender, EventArgs args)
    {
        if (DataContext is DashboardViewModel dashboard) dashboard.Refresh(DateTimeOffset.UtcNow);
    }
}
