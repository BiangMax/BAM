using BAM.Core;
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

internal sealed class EmptyDesktopData : IDesktopData
{
    public bool IsDemo => false;
    public IReadOnlyList<ScriptItem> Scripts => [];
    public IReadOnlyList<LogEntry> Logs => [];
    public bool HasResourceSource => false;
    public string? GetLogSourceNotice(string source) => null;
    public ResourceFrame? ReadResources(DateTimeOffset time) => null;
}

internal enum Tone { Neutral, Accent, Success, Warning, Danger }

internal sealed record ScriptItem(
    string Name, string Game, string Version, SymbolRegular Icon, Tone IdentityTone,
    ProcessState Process, GameTaskState Task,
    string InstallPath, string WorkingDirectory, string Arguments, string Capabilities)
{
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
    public string TaskLabel => Task switch
    {
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
    public string PrimaryAction => Process == ProcessState.Running ? "打开窗口" : "启动软件";
    public string OperationHint => "进程控制尚未接入";
}

internal sealed record ScriptUsage(double? Cpu, double? MemoryMiB, TimeSpan? Uptime);

internal sealed record ResourceFrame(
    DateTimeOffset Time, IReadOnlyList<ScriptUsage> Scripts, double? Cpu, double? MemoryMiB);

internal enum LogLevel { Information, Warning, Error }

internal sealed record LogEntry(DateTimeOffset Time, string Source, LogLevel Level, string Message, string Detail)
{
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
