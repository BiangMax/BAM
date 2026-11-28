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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBatchResult))] private BatchReport? _latestBatchResult;
    private readonly DashboardViewModel _dashboard;
    private readonly ScriptsViewModel _scripts;
    private readonly LogsViewModel _logs;
    private readonly LiveDesktopData? _live;
    private readonly SettingsViewModel _settings;

    public ShellViewModel(IDesktopData data, DashboardViewModel dashboard, ScriptsViewModel scripts,
        LogsViewModel logs, SettingsViewModel settings)
    {
        IsDemo = data.IsDemo;
        _dashboard = dashboard;
        _scripts = scripts;
        _logs = logs;
        _settings = settings;
        _live = data as LiveDesktopData;
        if (_live is not null) _live.Updated += (_, _) =>
        {
            OnPropertyChanged(nameof(OperationNotice));
            OnPropertyChanged(nameof(OperationNoticeDetails));
            LatestBatchResult = _live.LatestBatchResult;
            LaunchPairedCommand.NotifyCanExecuteChanged();
            CloseBatchCommand.NotifyCanExecuteChanged();
            MoveScriptUpCommand.NotifyCanExecuteChanged();
            MoveScriptDownCommand.NotifyCanExecuteChanged();
        };
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
    public string? OperationNoticeDetails => _live?.NoticeDetails ?? OperationNotice;
    public bool HasBatchResult => LatestBatchResult is not null;
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
    private void EditScript(ScriptItem? script) => ShowEditor(script);

    [RelayCommand]
    private void EditEmulatorMonitoring(ScriptItem? script)
    {
        if (script?.Installation is not null) ShowEditor(script, ProgramEditorSection.EmulatorMonitoring);
    }

    private void ShowEditor(ScriptItem? script, ProgramEditorSection section = ProgramEditorSection.General)
    {
        if (_live is null) return;
        var editor = new ProgramEditor(_live, script?.Installation, script?.EffectiveEntry, section) { Owner = System.Windows.Application.Current.MainWindow };
        editor.ShowDialog();
    }

    private bool CanLaunchPaired() => _live is not null && _dashboard.Cards.Any(card => card.Script.CanStartInBatch);

    [RelayCommand(CanExecute = nameof(CanLaunchPaired))]
    private async Task LaunchPaired()
    {
        if (_live is null) return;
        var requests = _dashboard.Cards.Select(card => card.Script).Where(script => script.CanStartInBatch)
            .Select(script => (Program: script.Installation!, Entry: script.EffectiveEntry!)).ToArray();
        if (requests.Length == 0) return;

        var details = string.Join("\n\n", requests.Select(request => request.Program.Kind == ProgramKind.Maa
            ? $"{request.Program.Name} · 由 MAA 按原设置启动模拟器\n" + DescribeLaunch("软件", request.Entry)
            : $"{request.Program.Name} · 等待 {request.Program.Emulator!.WaitSeconds} 秒\n" +
              DescribeLaunch("模拟器", request.Program.Emulator.Entry) + "\n" + DescribeLaunch("软件", request.Entry)));
        const string label = "批量启动";
        if (await Confirm(label, $"按总览卡片顺序启动以下 {requests.Length} 组。配对组先开模拟器，再开软件。" +
            $"软件可能按自己的设置自动运行任务。\n{PermissionLabel}。Windows 会按目标处理权限请求。\n\n{details}",
            label, scrollable: true))
            await _live.LaunchBatchAsync(requests);
    }

    private static string DescribeLaunch(string label, LaunchEntry entry) =>
        $"{label}　{entry.TargetPath}{(entry.RunAs ? "（请求管理员权限）" : "")}\n" +
        $"工作目录　{entry.WorkingDirectory}\n参数　{(entry.Arguments.Length == 0 ? "—" : entry.Arguments)}";

    private bool CanCloseBatch() => _live is not null && _dashboard.Cards.Any(card => card.Script.CanCloseInBatch);

    [RelayCommand(CanExecute = nameof(CanCloseBatch))]
    private async Task CloseBatch()
    {
        if (_live is null) return;
        var scripts = _dashboard.Cards.Select(card => card.Script).Where(script => script.CanCloseInBatch).ToArray();
        var targets = new List<(ScriptItem Script, EmulatorCloseTarget? Emulator)>();
        var details = new List<string>();
        foreach (var script in scripts)
        {
            var emulator = script.Installation?.EmulatorMonitor is not null ? await _live.PrepareEmulatorCloseAsync(script) : null;
            if (script.CanClose || emulator is not null) targets.Add((script, emulator));
            var detail = emulator is { } target ? $"模拟器 {target.Window.Title}（PID {target.Identity.Pid}）"
                : script.Installation?.EmulatorMonitor is null ? "仅关闭软件" : "模拟器未确认，跳过模拟器";
            details.Add($"{script.Name} · {(script.CanClose ? $"软件 PID {script.Identity!.Pid} · " : "软件已停止 · ")}{detail}");
        }
        if (targets.Count == 0) return;
        if (await Confirm("批量关闭", "按卡片顺序正常关闭以下软件及已确认的模拟器。正在执行的任务可能中断。\n\n" +
            string.Join("\n", details), "批量关闭", scrollable: true))
        {
            var report = await _live.CloseBatchAsync(targets);
            if (report is not null)
            {
                var dialog = CreateDialog("批量关闭结果", report, scrollable: true);
                dialog.CloseButtonText = "关闭";
                await dialog.ShowDialogAsync();
            }
        }
    }

    [RelayCommand]
    private void ShowBatchResult()
    {
        if (LatestBatchResult is not { } report) return;
        new BatchResultWindow { DataContext = report, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    private bool CanMoveScriptUp(ScriptItem? script) => _dashboard.CanMove(script, -1);
    private bool CanMoveScriptDown(ScriptItem? script) => _dashboard.CanMove(script, 1);

    [RelayCommand(CanExecute = nameof(CanMoveScriptUp))]
    private void MoveScriptUp(ScriptItem? script) => MoveScript(script, -1);

    [RelayCommand(CanExecute = nameof(CanMoveScriptDown))]
    private void MoveScriptDown(ScriptItem? script) => MoveScript(script, 1);

    private void MoveScript(ScriptItem? script, int offset)
    {
        if (!_dashboard.Move(script, offset)) return;
        MoveScriptUpCommand.NotifyCanExecuteChanged();
        MoveScriptDownCommand.NotifyCanExecuteChanged();
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
        else if (script.CanStart)
        {
            var paired = script.Installation?.Emulator is not null &&
                _dashboard.Cards.Any(card => card.Script.Installation?.Id == script.Installation.Id && card.PairEnabled);
            if (!paired || script.CanStartWithEmulator)
                await Launch(script, paired ? ProgramAction.StartWithEmulator : ProgramAction.Start);
        }
    }

    [RelayCommand]
    private void EditEmulatorStartup(ScriptItem? script)
    {
        if (script?.Installation is { SupportsEmulatorStartup: true }) ShowEditor(script, ProgramEditorSection.EmulatorStartup);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseScript(ScriptItem? script)
    {
        if (_live is null || script is not { CanClose: true, Identity: { } target }) return;
        if (_settings.Elevated == false || await Confirm("正常关闭软件",
            $"正常关闭「{script.Name}」（PID {target.Pid}）？\n{target.ImagePath}", "正常关闭"))
            await _live.ExecuteAsync(script, ProgramAction.Close);
    }

    [RelayCommand]
    private async Task CloseScriptWithEmulator(ScriptItem? script)
    {
        if (_live is null || script is not { CanCloseWithEmulator: true, Identity: { } identity }) return;
        if (await _live.PrepareEmulatorCloseAsync(script) is not { } target) return;
        if (await Confirm("关闭软件和模拟器",
            $"BAM 将先正常关闭「{script.Name}」（PID {identity.Pid}），确认退出后再关闭以下模拟器。正在进行的任务可能中断。\n\n" +
            $"模拟器　{target.Window.Title}（PID {target.Identity.Pid}）\n{target.Identity.ImagePath}", "关闭两者"))
            await _live.ExecuteAsync(script, ProgramAction.CloseWithEmulator, confirmedEmulator: target);
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
        if (_live is null || script.Installation is not { } program || script.EffectiveEntry is not { } entry) return;
        var label = action == ProgramAction.Restart ? "重启软件" : "启动软件";
        var notice = program.Kind == ProgramKind.Maa
            ? "MAA 可能按原配置自动运行游戏任务并启动模拟器。"
            : "软件启动后可能按原配置自动执行任务。";
        var details = $"启动目标　{entry.TargetPath}\n工作目录　{entry.WorkingDirectory}\n参数　{(entry.Arguments.Length == 0 ? "—" : entry.Arguments)}";
        if (action == ProgramAction.Restart && script.Identity is { } old)
            details = $"将关闭　{old.ImagePath}（PID {old.Pid}）\n" + details;
        if (action == ProgramAction.StartWithEmulator && program.Emulator is { } emulator)
            details = DescribeLaunch("模拟器", emulator.Entry) + $"\n等待 {emulator.WaitSeconds} 秒后启动软件。\n\n" + details;
        if (await Confirm(label, $"继续{label[..2]}「{script.Name}」？{notice}\n{PermissionLabel}。Windows 可能请求此目标的权限。\n\n{details}", label))
            await _live.ExecuteAsync(script, action, entry);
    }

    private static async Task<bool> Confirm(string title, string content, string affirmative, bool scrollable = false)
    {
        var dialog = CreateDialog(title, content, scrollable);
        dialog.PrimaryButtonText = affirmative;
        dialog.CloseButtonText = "取消";
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private static Wpf.Ui.Controls.MessageBox CreateDialog(string title, string content, bool scrollable)
    {
        var body = new System.Windows.Controls.TextBlock { Text = content, FontSize = 14 };
        body.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "Bam.BodyCopy");
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Title = title,
            Content = scrollable ? new System.Windows.Controls.ScrollViewer
            {
                Content = body,
                MaxHeight = 340,
                MaxWidth = 480,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            } : body,
            UseLayoutRounding = true
        };
        dialog.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "Bam.Font");
        return dialog;
    }
}
