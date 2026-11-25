using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace BAM.Desktop;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
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

        var services = new ServiceCollection();
        services.AddSingleton<VerificationWindow>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var window = _services.GetRequiredService<VerificationWindow>();
        MainWindow = window;
        window.Show();
        _instance.Listen(() => Dispatcher.InvokeAsync(() =>
        {
            if (Dispatcher.HasShutdownStarted || !window.IsLoaded) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.Dispose(); }
        finally { _instance?.Dispose(); }
        base.OnExit(e);
    }
}
