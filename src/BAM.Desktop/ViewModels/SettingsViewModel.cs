using System.Diagnostics;
using BAM.Desktop.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BAM.Desktop.ViewModels;

internal sealed record ThemeOption(ThemeChoice Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed partial class SettingsViewModel : ObservableObject
{
    private const string Repository = "https://github.com/BiangMax/BAM";
    private readonly SettingsStore _store;
    private readonly string? _loadNotice;
    private bool _trayIntroSeen;
    private int _saveRevision;

    [ObservableProperty] private ThemeChoice _theme;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _collectResources;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _linkNotice;

    public SettingsViewModel(SettingsStore store, DesktopPreferences preferences, string? loadNotice, bool canCollectResources,
        bool? elevated, MaaLoopbackViewModel? loopback = null)
    {
        _store = store;
        _loadNotice = loadNotice;
        _notice = loadNotice;
        _theme = preferences.Theme;
        _reduceMotion = preferences.ReduceMotion;
        _closeToTray = preferences.CloseToTray;
        _collectResources = preferences.CollectResources;
        CanCollectResources = canCollectResources;
        Elevated = elevated;
        Loopback = loopback;
        _trayIntroSeen = preferences.TrayIntroSeen;
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [new(ThemeChoice.Dark, "深色"), new(ThemeChoice.Light, "浅色"), new(ThemeChoice.System, "跟随系统")];

    public string VersionLabel => $"BAM {ShellViewModel.Version}";
    public bool CanCollectResources { get; }
    public bool? Elevated { get; }
    public MaaLoopbackViewModel? Loopback { get; }
    public bool HasMaaLoopback => Loopback is not null;
    public string ResourceDescription => CanCollectResources
        ? "采集已确认主进程的 CPU 和工作集。暂停后继续观测进程状态。" : "演示模式使用合成曲线。";
    public bool MarkTrayIntroSeen()
    {
        if (_trayIntroSeen) return false;
        _trayIntroSeen = true;
        Save();
        return true;
    }

    partial void OnThemeChanged(ThemeChoice value) => Save();
    partial void OnReduceMotionChanged(bool value) => Save();
    partial void OnCloseToTrayChanged(bool value) => Save();
    partial void OnCollectResourcesChanged(bool value) => Save();

    private void Save() => _ = SaveAsync(++_saveRevision, new(1, Theme, ReduceMotion, CloseToTray, _trayIntroSeen, CollectResources));

    private async Task SaveAsync(int revision, DesktopPreferences settings)
    {
        var error = await _store.QueueSave(settings);
        if (revision == _saveRevision) Notice = error ?? _loadNotice;
    }

    [RelayCommand]
    private void OpenRepository()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Repository) { UseShellExecute = true });
            LinkNotice = null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            LinkNotice = "无法打开浏览器。仓库地址为 github.com/BiangMax/BAM。";
        }
    }
}
