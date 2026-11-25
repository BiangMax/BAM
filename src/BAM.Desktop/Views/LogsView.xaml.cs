using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BAM.Desktop.ViewModels;

namespace BAM.Desktop.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;
    private DispatcherOperation? _pendingScroll;

    public LogsView()
    {
        InitializeComponent();
        Loaded += Connect;
        Unloaded += Disconnect;
    }

    private void Connect(object sender, RoutedEventArgs args)
    {
        if (DataContext is not LogsViewModel model) return;
        _viewModel = model;
        model.Entries.CollectionChanged += EntriesChanged;
        model.PropertyChanged += FollowChanged;
        ScrollToLatest();
    }

    private void Disconnect(object sender, RoutedEventArgs args)
    {
        _pendingScroll?.Abort();
        if (_viewModel is not { } model) return;
        model.Entries.CollectionChanged -= EntriesChanged;
        model.PropertyChanged -= FollowChanged;
        _viewModel = null;
    }

    private void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScrollToLatest();

    private void FollowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LogsViewModel.Follow)) ScrollToLatest();
    }

    private void ScrollToLatest()
    {
        if (_viewModel is not { Follow: true } model || _pendingScroll is { Status: DispatcherOperationStatus.Pending }) return;
        _pendingScroll = Dispatcher.InvokeAsync(() =>
        {
            if (model.Follow && model.Entries.Cast<object>().FirstOrDefault() is { } first)
                LogList.ScrollIntoView(first);
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.OriginalSource is ScrollViewer && args.VerticalOffset > 1 && args.VerticalChange > 0
            && args.ExtentHeightChange == 0 && _viewModel is { Follow: true } model)
            model.Follow = false;
    }
}
