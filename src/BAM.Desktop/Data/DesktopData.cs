using BAM.Core;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace BAM.Desktop.Data;

internal interface IDesktopData
{
    bool IsDemo { get; }
    IReadOnlyList<ScriptItem> Scripts { get; }
    IReadOnlyList<LogEntry> Logs { get; }
    bool HasResourceSource { get; }
    string? GetLogSourceNotice(string source);
    ResourceFrame? ReadResources(DateTimeOffset time);
    IReadOnlyList<ResourceFrame> ReadRecentResources(DateTimeOffset time) => [];
}

internal enum Tone { Neutral, Accent, Success, Warning, Danger }

internal sealed record ScriptItem(
    string Name, string Game, string Version, SymbolRegular Icon, Tone IdentityTone,
    ProcessState Process, GameTaskState Task,
    string InstallPath, string WorkingDirectory, string Arguments, string Capabilities)
{
    public ProgramInstallation? Installation { get; init; }
    public ImageSource? ActualIcon { get; init; }
    public string? VersionDetail { get; init; }
    public string? Issue { get; init; }
    public string? TaskHint { get; init; }
    public ProcessIdentity? Identity { get; init; }
    public nint WindowHandle { get; init; }
    public bool CanStart { get; init; }
    public bool CanActivate { get; init; }
    public bool CanClose { get; init; }
    public bool CanRestart { get; init; }
    public bool CanForce { get; init; }
    public bool RedactDetails { get; init; }
    public string EntryPath => Private(Installation?.Entry.OriginalPath ?? InstallPath);
    public string? LaunchTarget => Installation is { } installation ? Private(installation.Entry.TargetPath) : null;
    public string DisplayInstallPath => Private(InstallPath);
    public string DisplayWorkingDirectory => Private(WorkingDirectory);
    public string DisplayArguments => Arguments == "—" ? Arguments : Private(Arguments);
    public string? WorkerPaths => Installation is { } installation ? Private(string.Join(Environment.NewLine, installation.WorkerPaths)) : null;
    private string Private(string value) => RedactDetails ? "已脱敏" : value;
    public bool HasLiveDetails => Installation is not null;
    public bool NeedsRelocation { get; init; }
    public bool CanEditFromDetails => HasLiveDetails && !NeedsRelocation;
    public string AccessibleName => $"{Name}，{Game}";
    public string ProcessLabel => Process switch
    {
        ProcessState.Running => "已启动",
        ProcessState.Stopped => "已停止",
        ProcessState.Starting => "正在启动",
        ProcessState.Stopping => "正在关闭",
        ProcessState.ExitedUnexpectedly => "疑似异常退出",
        _ => "状态未知"
    };
    public Tone ProcessTone => Process switch
    {
        ProcessState.Running => Tone.Success,
        ProcessState.Starting or ProcessState.Stopping => Tone.Accent,
        ProcessState.ExitedUnexpectedly => Tone.Warning,
        _ => Tone.Neutral
    };
    public string TaskScopeLabel => TaskHint is not null && Task != GameTaskState.Unsupported ? "框架任务" : "游戏任务";
    public string TaskLabel => Task switch
    {
        GameTaskState.Unsupported when TaskHint is not null => "版本不支持",
        GameTaskState.Unsupported => "未接入任务检测",
        GameTaskState.Idle => "空闲",
        GameTaskState.Starting => "正在启动",
        GameTaskState.Running => "执行中",
        GameTaskState.Stopping => "正在停止",
        GameTaskState.Completed => "已完成",
        GameTaskState.Cancelled => "已取消",
        GameTaskState.Failed => "失败",
        _ => "状态未知"
    };
    public Tone TaskTone => Task switch
    {
        GameTaskState.Running or GameTaskState.Starting or GameTaskState.Stopping => Tone.Accent,
        GameTaskState.Completed => Tone.Success,
        GameTaskState.Failed => Tone.Danger,
        _ => Tone.Neutral
    };
    public bool CanPrimary => NeedsRelocation || CanStart || CanActivate;
    public bool ShowEmulatorAction => Installation?.SupportsEmulatorStartup == true;
    public EmulatorResourceUsage? EmulatorUsage { get; init; }
    public bool ShowEmulatorUsage => EmulatorUsage is not null;
    public string? EmulatorUsageLabel => EmulatorUsage is { } usage
        ? $"模拟器　CPU {(usage.Cpu is { } cpu ? $"{cpu:0.0}%" : "—")} · 内存 {(usage.WorkingSetMiB is { } memory ? $"{memory:0} MiB" : "—")}"
        : null;
    public bool CanUseEmulatorAction => ShowEmulatorAction && (Installation?.Emulator?.Entry is null || CanStart);
    public string EmulatorAction => Installation?.Emulator?.Entry is null ? "设置模拟器启动" : "启动模拟器和软件";
    public string EmulatorHint => Installation?.Emulator is { Entry: not null } setup
        ? CanStart ? $"先打开选定的模拟器，等待 {setup.WaitSeconds} 秒，再启动软件。" : OperationHint
        : "为此软件选择对应模拟器实例的快捷方式。";
    public string PrimaryAction => NeedsRelocation ? "重新定位" :
        Identity is not null || Process is ProcessState.Running or ProcessState.Stopping ? "打开窗口" : "启动软件";
    public string OperationHint => Issue ?? (Installation is null ? "演示数据仅供预览。" :
        CanStart ? "软件已停止。窗口与退出操作需要已确认的运行实例。" : "可操作已确认的软件实例。");
}

internal sealed record ScriptUsage(double? Cpu, double? MemoryMiB, TimeSpan? Uptime);

internal sealed record ResourceFrame(
    DateTimeOffset Time, IReadOnlyList<ScriptUsage> Scripts, double? Cpu, double? MemoryMiB);

internal enum LogLevel { Information, Warning, Error }

internal sealed record LogEntry(DateTimeOffset Time, string Source, LogLevel Level, string Message, string Detail)
{
    public Guid? InstallationId { get; init; }
    public string Clock => Time.ToLocalTime().ToString("HH:mm:ss");
    public string Timestamp => Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    public string LevelLabel => Level switch
    {
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息"
    };
    public Tone Tone => Level switch
    {
        LogLevel.Warning => Tone.Warning,
        LogLevel.Error => Tone.Danger,
        _ => Tone.Accent
    };
    public SymbolRegular Icon => Level switch
    {
        LogLevel.Warning => SymbolRegular.Warning24,
        LogLevel.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24
    };
    public string AccessibleName => $"{Clock}，{Source}，{LevelLabel}，{Message}";
    public string ClipboardText => $"{Timestamp}  {Source}  {LevelLabel}  {Message}{Environment.NewLine}{Detail}";
    public override string ToString() => AccessibleName;
}
