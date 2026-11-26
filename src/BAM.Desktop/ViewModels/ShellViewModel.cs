using System.Reflection;
using System.ComponentModel;
using BAM.Core;
using BAM.Desktop.Data;
using BAM.Desktop.Views;
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
    private readonly LiveDesktopData? _live;
    private readonly SettingsViewModel _settings;

    public ShellViewModel(IDesktopData data, DashboardViewModel dashboard, ScriptsViewModel scripts,
        LogsViewModel logs, SettingsViewModel settings)
    {
        IsDemo = data.IsDemo;
        _scripts = scripts;
        _logs = logs;
        _settings = settings;
        _live = data as LiveDesktopData;
        if (_live is not null) _live.Updated += (_, _) => OnPropertyChanged(nameof(OperationNotice));
        _settings.PropertyChanged += OnSettingsChanged;
        _logs.PropertyChanged += OnLogsChanged;
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
    public string PermissionLabel => _settings.Elevated switch
    {
        true => "管理员权限 · 仅会话保存",
        false => "普通权限",
        null => "权限未知 · 仅会话保存"
    };
    public string MonitoringStatus => IsDemo ? "合成采样" : _settings.CollectResources ? "本地进程监控" : "资源采样已暂停";
    public string? OperationNotice => _live?.Notice;
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
        _scripts.Selected = _scripts.Items.FirstOrDefault(item => ReferenceEquals(item, script) ||
            (script.Installation is not null && item.Installation?.Id == script.Installation.Id));
        OpenScripts();
    }

    [RelayCommand]
    private void OpenScriptLogs(ScriptItem? script)
    {
        if (script is null) return;
        _logs.ShowScript(script);
        OpenLogs();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.CollectResources)) OnPropertyChanged(nameof(MonitoringStatus));
    }

    private void OnLogsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LogsViewModel.ErrorCount)) return;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorLabel));
    }

    [RelayCommand] private Task Discover() => _live?.DiscoverAsync() ?? Task.CompletedTask;
    [RelayCommand] private void AddScript() => EditScript(null);

    [RelayCommand]
    private void EditScript(ScriptItem? script) => ShowEditor(script, focusEmulator: false);

    private void ShowEditor(ScriptItem? script, bool focusEmulator)
    {
        if (_live is null) return;
        var editor = new ProgramEditor(_live, script?.Installation, focusEmulator) { Owner = System.Windows.Application.Current.MainWindow };
        editor.ShowDialog();
    }

    [RelayCommand]
    private async Task RemoveScript(ScriptItem? script)
    {
        if (_live is null || script?.Installation is not { } program) return;
        if (await Confirm("移除管理记录", $"从 BAM 移除「{script.Name}」的记录？软件和当前进程会保留。", "移除记录"))
            await _live.RemoveAsync(program.Id);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Primary(ScriptItem? script)
    {
        if (_live is null || script is null) return;
        if (script.NeedsRelocation) EditScript(script);
        else if (script.CanActivate) await _live.ExecuteAsync(script, ProgramAction.Activate);
        else if (script.CanStart) await Launch(script, ProgramAction.Start);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartWithEmulator(ScriptItem? script)
    {
        if (_live is null || script is not { CanUseEmulatorAction: true, Installation: { } program }) return;
        if (program.Emulator is not { Entry: { } start } emulator)
        {
            ShowEditor(script, focusEmulator: true);
            return;
        }

        const string label = "启动模拟器和软件";
        var entry = program.Entry;
        var details = $"模拟器　{start.TargetPath}\n工作目录　{start.WorkingDirectory}\n参数　{start.Arguments}\n\n" +
            $"软件　{entry.TargetPath}\n工作目录　{entry.WorkingDirectory}\n参数　{entry.Arguments}";
        if (await Confirm(label, $"先请求启动选定的模拟器，等待 {emulator.WaitSeconds} 秒，然后启动「{script.Name}」。" +
            "等待时间由你设置。软件可能按原配置自动运行任务。\n" +
            $"{PermissionLabel}。Windows 可能请求目标的权限。\n\n{details}", label))
            await _live.ExecuteAsync(script, ProgramAction.StartWithEmulator);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseScript(ScriptItem? script)
    {
        if (_live is null || script is not { CanClose: true, Identity: { } target }) return;
        if (_settings.Elevated == false || await Confirm("正常关闭软件",
            $"正常关闭「{script.Name}」（PID {target.Pid}）？\n{target.ImagePath}", "正常关闭"))
            await _live.ExecuteAsync(script, ProgramAction.Close);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RestartScript(ScriptItem? script) => script is { CanRestart: true }
        ? Launch(script, ProgramAction.Restart) : Task.CompletedTask;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ForceScript(ScriptItem? script)
    {
        if (_live is null || script is not { CanForce: true, Identity: { } target }) return;
        if (await Confirm("强制结束软件", $"强制结束「{script.Name}」（PID {target.Pid}）可能丢失未保存的数据，并中断正在执行的任务。\n{target.ImagePath}", "强制结束"))
            await _live.ExecuteAsync(script, ProgramAction.Force);
    }

    private async Task Launch(ScriptItem script, ProgramAction action)
    {
        if (_live is null || script.Installation is not { } program) return;
        var label = action == ProgramAction.Restart ? "重启软件" : "启动软件";
        var notice = program.Kind == ProgramKind.Maa
            ? "MAA 可能按原配置自动运行游戏任务并启动模拟器。"
            : "软件启动后可能按原配置自动执行任务。";
        var entry = program.Entry;
        var details = $"启动目标　{entry.TargetPath}\n工作目录　{entry.WorkingDirectory}\n参数　{script.Arguments}";
        if (action == ProgramAction.Restart && script.Identity is { } old)
            details = $"将关闭　{old.ImagePath}（PID {old.Pid}）\n" + details;
        if (await Confirm(label, $"继续{label[..2]}「{script.Name}」？{notice}\n{PermissionLabel}。Windows 可能请求此目标的权限。\n\n{details}", label))
            await _live.ExecuteAsync(script, action);
    }

    private static async Task<bool> Confirm(string title, string content, string affirmative)
    {
        var body = new System.Windows.Controls.TextBlock { Text = content, FontSize = 14 };
        body.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Bam.BodyCopy");
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Title = title,
            Content = body,
            UseLayoutRounding = true,
            PrimaryButtonText = affirmative,
            CloseButtonText = "取消"
        };
        dialog.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "Bam.Font");
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
