using System.Reflection;
using BAM.Desktop.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace BAM.Desktop.ViewModels;

internal sealed record NavigationItem(string Label, SymbolRegular Icon, object ViewModel);

internal sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] private NavigationItem _selected;
    private readonly ScriptsViewModel _scripts;
    private readonly LogsViewModel _logs;

    public ShellViewModel(IDesktopData data, DashboardViewModel dashboard, ScriptsViewModel scripts,
        LogsViewModel logs, SettingsViewModel settings)
    {
        IsDemo = data.IsDemo;
        _scripts = scripts;
        _logs = logs;
        Navigation =
        [
            new("总览", SymbolRegular.Grid24, dashboard),
            new("脚本管理", SymbolRegular.Apps24, scripts),
            new("运行日志", SymbolRegular.DocumentText24, logs),
            new("设置", SymbolRegular.Settings24, settings)
        ];
        _selected = Navigation[0];
    }

    public IReadOnlyList<NavigationItem> Navigation { get; }
    public bool IsDemo { get; }
    public string MonitoringStatus => IsDemo ? "合成采样" : "监控未接入";
    public bool HasErrors => _logs.ErrorCount > 0;
    public string ErrorLabel => $"{_logs.ErrorCount} 条错误";
    public static string Version { get; } = typeof(ShellViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public string VersionLabel => $"BAM {Version}";

    [RelayCommand] public void OpenOverview() => Selected = Navigation[0];
    [RelayCommand] private void OpenScripts() => Selected = Navigation[1];
    [RelayCommand] private void OpenLogs() => Selected = Navigation[2];

    [RelayCommand]
    private void OpenErrors()
    {
        _logs.ShowErrors();
        OpenLogs();
    }

    [RelayCommand]
    private void OpenScriptDetails(ScriptItem? script)
    {
        if (script is null) return;
        _scripts.Selected = script;
        OpenScripts();
    }

    [RelayCommand]
    private void OpenScriptLogs(ScriptItem? script)
    {
        if (script is null) return;
        _logs.Source = script.Name;
        OpenLogs();
    }
}
