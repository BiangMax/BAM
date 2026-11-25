using System.Windows;
using BAM.Desktop.Data;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BAM.Desktop;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;
    private SettingsStore? _settingsStore;
    private readonly CancellationTokenSource _lifetime = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instance = new SingleInstance();
            if (!_instance.IsPrimary)
            {
                Shutdown(_instance.RequestActivation() ? 0 : 2);
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("无法访问 BAM 的单实例对象。请使用同一用户和权限级别打开 BAM。", "BAM");
            Shutdown(3);
            return;
        }

        IDesktopData data = new EmptyDesktopData();
#if BAM_DEMO
        if (e.Args.Contains("--demo", StringComparer.Ordinal)) data = new DemoDesktopData();
#endif
        _settingsStore = new SettingsStore(data.IsDemo);
        var cancellation = _lifetime.Token;
        DesktopPreferences preferences;
        string? notice;
        try { (preferences, notice) = await _settingsStore.LoadAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        if (cancellation.IsCancellationRequested) return;

        var services = new ServiceCollection();
        services.AddSingleton(data);
        services.AddSingleton(new SettingsViewModel(_settingsStore, preferences, notice));
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ScriptsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ThemeController>();
        services.AddSingleton(service => new MainWindow(service.GetRequiredService<ShellViewModel>(),
            service.GetRequiredService<SettingsViewModel>(), service.GetRequiredService<ThemeController>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
#if BAM_DEMO
        var captureArgument = Array.IndexOf(e.Args, "--capture-dir");
        if (captureArgument >= 0 && captureArgument + 1 < e.Args.Length)
            window.EnableCapture(e.Args[captureArgument + 1]);
#endif
        window.Show();
        _instance.Listen(() => Dispatcher.InvokeAsync(() =>
        {
            if (Dispatcher.HasShutdownStarted || !window.IsLoaded) return;
            window.Restore();
        }));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && MainWindow is MainWindow window) window.AllowClose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _lifetime.Cancel();
            _settingsStore?.FlushAsync().GetAwaiter().GetResult();
            _services?.Dispose();
        }
        finally
        {
            _lifetime.Dispose();
            _instance?.Dispose();
        }
        base.OnExit(e);
    }
}
