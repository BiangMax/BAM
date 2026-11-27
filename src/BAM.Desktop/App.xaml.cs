using System.Windows;
using System.IO;
using BAM.Adapters;
using BAM.Core;
using BAM.Desktop.Data;
using BAM.Desktop.Settings;
using BAM.Desktop.ViewModels;
using BAM.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BAM.Desktop;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;
    private SettingsStore? _settingsStore;
    private LiveDesktopData? _live;
    private Task? _drain;
    private readonly CancellationTokenSource _lifetime = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? verificationProfile = null;
        var discover = true;
        var instanceName = "Biang.BAM";
#if BAM_VERIFICATION
        var profileArgument = Array.IndexOf(e.Args, "--verification-profile");
        if (profileArgument >= 0 && profileArgument + 1 < e.Args.Length)
        {
            verificationProfile = Path.GetFullPath(e.Args[profileArgument + 1]);
            discover = !e.Args.Contains("--verification-skip-discovery", StringComparer.Ordinal);
            if (!discover)
            {
                var profileBytes = System.Text.Encoding.UTF8.GetBytes(verificationProfile);
                instanceName += ".Verification." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileBytes));
            }
        }
#endif
        var elevated = WindowsProgramPlatform.IsCurrentProcessElevated();
        try
        {
            _instance = new SingleInstance(elevated == true, instanceName);
            if (!_instance.IsPrimary)
            {
                if (_instance.RequestActivation())
                {
                    Shutdown(0);
                    return;
                }

                ShowInstanceConflict(elevated);
                Shutdown(2);
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            ShowInstanceConflict(elevated);
            Shutdown(3);
            return;
        }

        IDesktopData? demo = null;
#if BAM_DEMO
        if (e.Args.Contains("--demo", StringComparer.Ordinal)) demo = new DemoDesktopData();
#endif
        var profile = verificationProfile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAM");
        var sessionOnly = elevated != false;
        _settingsStore = new SettingsStore(demo is not null, Path.Combine(profile, "settings.json"), sessionOnly);
        var cancellation = _lifetime.Token;
        DesktopPreferences preferences;
        string? notice;
        try { (preferences, notice) = await _settingsStore.LoadAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        if (cancellation.IsCancellationRequested) return;

        var services = new ServiceCollection();
        if (demo is not null) services.AddSingleton(demo);
        else
        {
            services.AddSingleton<IProgramFiles, WindowsProgramFiles>();
            services.AddSingleton<IProgramPlatform, WindowsProgramPlatform>();
            services.AddSingleton<IProgramStore>(new LocalProgramStore(profile, sessionOnly));
            services.AddSingleton<IProgramEventStore>(new LocalProgramEventStore(Path.Combine(profile, "logs"), sessionOnly));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<InstallationCatalog>();
            services.AddSingleton<ProgramMonitor>();
            if (e.Args.Contains("--observe-maaend-tasks", StringComparer.Ordinal))
            {
                services.AddSingleton<ITaskLogSource, WindowsTaskLogSource>();
                services.AddSingleton<MaaEndTaskObserver>();
            }
            services.AddSingleton<IDesktopData, LiveDesktopData>();
        }
        if (demo is null && sessionOnly)
            notice = string.Join(" ", new[] { notice, "当前权限下，设置、安装修改和新增日志仅保留在本次会话。" }
                .Where(value => value is not null));
        services.AddSingleton(new SettingsViewModel(_settingsStore, preferences, notice, demo is null, elevated));
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ScriptsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ThemeController>();
        services.AddSingleton(service => new MainWindow(service.GetRequiredService<ShellViewModel>(),
            service.GetRequiredService<SettingsViewModel>(), service.GetRequiredService<ThemeController>(),
            service.GetRequiredService<IDesktopData>() as LiveDesktopData, PrepareExitAsync));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
#if BAM_DEMO || BAM_VERIFICATION
        var captureArgument = Array.IndexOf(e.Args, "--capture-dir");
        if (captureArgument >= 0 && captureArgument + 1 < e.Args.Length)
            window.EnableCapture(e.Args[captureArgument + 1]);
#endif
        _live = _services.GetRequiredService<IDesktopData>() as LiveDesktopData;
#if BAM_VERIFICATION
        if (_live is not null)
        {
            _live.RedactDetails = captureArgument >= 0;
            if (verificationProfile is not null && !discover && e.Args.Contains("--verification-log-stress", StringComparer.Ordinal))
                _live.EnableLogStress();
        }
#endif
        window.Show();
        _live?.Start(action => Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Background, cancellation).Task, discover);
        _instance.Listen(() => Dispatcher.InvokeAsync(() =>
        {
            if (Dispatcher.HasShutdownStarted || !window.IsLoaded) return;
            window.Restore();
        }));
    }

    private static void ShowInstanceConflict(bool? elevated)
    {
        var target = elevated == true ? "再右键选择「以管理员身份运行」。" : "再启动 BAM。";
        MessageBox.Show("无法打开本次 BAM 窗口，可能已有其他权限或会话的 BAM 在运行。请从现有 BAM 的托盘菜单选择「退出 BAM」，" +
            target, "BAM · 已有实例", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private Task DrainAsync()
    {
        _lifetime.Cancel();
        return _drain ??= Task.WhenAll(_live?.StopAsync() ?? Task.CompletedTask,
            _settingsStore?.FlushAsync() ?? Task.CompletedTask);
    }

    private Task PrepareExitAsync() => WaitForShutdownAsync(DrainAsync(), TimeSpan.FromSeconds(3));

    internal static async Task WaitForShutdownAsync(Task drain, TimeSpan timeout)
    {
        try { await drain.WaitAsync(timeout); }
        catch (TimeoutException) { }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && MainWindow is MainWindow window)
        {
            window.AllowSessionEnd();
            _ = DrainAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (DrainAsync().IsCompleted) _services?.Dispose();
        }
        finally
        {
            _lifetime.Dispose();
            _instance?.Dispose();
        }
        base.OnExit(e);
    }
}
