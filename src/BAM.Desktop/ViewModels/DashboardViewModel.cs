using BAM.Core;
using BAM.Desktop.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace BAM.Desktop.ViewModels;

internal sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IDesktopData _data;
    private DateTimeOffset? _lastSample;
    private ProgramFrame? _lastFrame;

    [ObservableProperty] private bool _showMemory;
    [ObservableProperty] private int _chartRevision;
    [ObservableProperty] private string _sampleLabel = "等待采样";
    [ObservableProperty] private string _cpu = "—";
    [ObservableProperty] private string _memory = "—";
    [ObservableProperty] private string _chartDetail = "暂无资源样本";

    public DashboardViewModel(IDesktopData data)
    {
        _data = data;
        Cards = data.Scripts.Select(script => new ScriptCardViewModel(script)).ToArray();
        RecentEvents = data.Logs.Take(3).ToArray();
        SampleLabel = data.HasResourceSource ? "等待采样" : "暂无采样";
        ChartDetail = data.HasResourceSource ? "等待资源样本" : "接入进程采样后可查看资源曲线";
        if (data is LiveDesktopData live) live.Updated += (_, _) => UpdateLive(live);
    }

    public ResourceHistory History { get; } = new();
    public bool IsDemo => _data.IsDemo;
    public bool HasResourceSource => _data.HasResourceSource;
    public IReadOnlyList<ScriptCardViewModel> Cards { get; private set; }
    public IReadOnlyList<LogEntry> RecentEvents { get; private set; }
    public bool HasScripts => Cards.Count > 0;
    public bool HasEvents => RecentEvents.Count > 0;
    public string ProcessCount => _data is LiveDesktopData live
        ? $"{live.Frame?.ConfirmedWorkers ?? 0} / {Cards.Count}" : HasResourceSource
            ? $"{_data.Scripts.Count(x => x.Process == ProcessState.Running)} / {Cards.Count}" : $"— / {Cards.Count}";
    public string ChartScope => IsDemo ? "演示脚本主进程" : "已确认脚本主进程";
    public string ResourceHint => HasResourceSource ? ChartScope : "等待接入进程采样";
    public string ProcessHint => _lastFrame is { UnknownPrograms: > 0 } frame
        ? $"{frame.UnknownPrograms} 个安装待确认" : "软件进程";
    public string TaskCount => _lastFrame?.Programs.Count(program => program.Task == GameTaskState.Running) is > 0 and var count
        ? count.ToString() : "—";
    public string TaskSummary => _lastFrame?.Programs.Any(program => program.Task == GameTaskState.Running) == true
        ? "已确认执行中的框架任务"
        : _lastFrame?.Programs.Any(program => program.Task != GameTaskState.Unsupported) == true
            ? "暂无已确认执行中的框架任务" : "暂无可检测任务";
    public string CpuScope => CoverageHint(_lastFrame?.CpuCoverage);
    public string MemoryScope => CoverageHint(_lastFrame?.MemoryCoverage);
    private string CoverageHint(int? covered) => _lastFrame is { ConfirmedWorkers: > 0 } frame &&
        (covered != frame.ConfirmedWorkers || frame.UnknownPrograms > 0)
        ? $"已采样 {covered}/{frame.ConfirmedWorkers} 个确认实例" : ResourceHint;
    public bool ShowCpu
    {
        get => !ShowMemory;
        set { if (value) ShowMemory = false; }
    }

    partial void OnShowMemoryChanged(bool value) => OnPropertyChanged(nameof(ShowCpu));

    private void UpdateLive(LiveDesktopData live)
    {
        if (live.Frame is not { } frame || ReferenceEquals(frame, _lastFrame)) return;
        _lastFrame = frame;
        var cards = Cards.ToDictionary(card => card.Script.Installation!.Id);
        var next = new List<ScriptCardViewModel>();
        for (var index = 0; index < live.Scripts.Count; index++)
        {
            var item = live.Scripts[index];
            if (!cards.TryGetValue(item.Installation!.Id, out var card)) card = new(item);
            card.Script = item;
            var status = frame.Programs[index];
            card.Update(new(status.Cpu, status.WorkingSetMiB, status.Uptime));
            next.Add(card);
        }
        if (!Cards.SequenceEqual(next))
        {
            Cards = next;
            OnPropertyChanged(nameof(Cards));
            OnPropertyChanged(nameof(HasScripts));
        }
        if (!RecentEvents.SequenceEqual(live.Logs.Take(3)))
        {
            RecentEvents = live.Logs.Take(3).ToArray();
            OnPropertyChanged(nameof(RecentEvents));
            OnPropertyChanged(nameof(HasEvents));
        }
        OnPropertyChanged(nameof(ProcessCount));
        OnPropertyChanged(nameof(ProcessHint));
        OnPropertyChanged(nameof(TaskCount));
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(CpuScope));
        OnPropertyChanged(nameof(MemoryScope));
        var hadData = History.HasData;
        History.Add(new(frame.ObservedAt, frame.Cpu, frame.WorkingSetMiB, frame.Elapsed));
        Cpu = Format(frame.Cpu, "0.0", "%");
        Memory = Format(frame.WorkingSetMiB, "0", " MiB");
        if (frame.Cpu is not null || frame.WorkingSetMiB is not null) _lastSample = frame.ObservedAt;
        SampleLabel = _lastSample is { } latest ? $"最近有效采样 {latest.ToLocalTime():HH:mm:ss}" : "暂无有效采样";
        ChartDetail = $"最近 120 秒 · CPU {frame.CpuCoverage}/{frame.ConfirmedWorkers} · 工作集 {frame.MemoryCoverage}/{frame.ConfirmedWorkers}" +
            (frame.UnknownPrograms > 0 ? $" · {frame.UnknownPrograms} 个安装状态待确认" : "");
        if (hadData || History.HasData) ChartRevision++;
    }

    public void Refresh(DateTimeOffset now, bool resume = false)
    {
        if (!HasResourceSource || now <= History.End) return;
        if (resume || History.Count == 0)
        {
            foreach (var recent in _data.ReadRecentResources(now))
                History.Add(new(recent.Time, recent.Cpu, recent.MemoryMiB));
        }

        var frame = _data.ReadResources(now);
        History.Add(new(now, frame?.Cpu, frame?.MemoryMiB));
        for (var index = 0; index < Cards.Count; index++)
            Cards[index].Update(frame?.Scripts.ElementAtOrDefault(index));

        Cpu = Format(frame?.Cpu, "0.0", "%");
        Memory = Format(frame?.MemoryMiB, "0", " MiB");
        if (frame is { Cpu: not null, MemoryMiB: not null }) _lastSample = frame.Time;
        SampleLabel = _lastSample is { } latest ? $"最近采样 {latest.ToLocalTime():HH:mm:ss}" : "暂无采样";
        ChartDetail = $"最近 120 秒 · {SampleLabel} · CPU {Cpu} · 工作集 {Memory}";
        ChartRevision++;
    }

    internal static string Format(double? value, string format, string unit)
        => value is double number ? $"{number.ToString(format)}{unit}" : "—";
}

internal sealed partial class ScriptCardViewModel(ScriptItem script) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CpuHint), nameof(MemoryHint), nameof(UptimeHint))]
    private ScriptItem _script = script;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CpuHint))] private string _cpu = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MemoryHint))] private string _memory = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UptimeHint))] private string _uptime = "—";
    private string MissingValueHint => Script.Process == ProcessState.Stopped ? "软件已停止" : "暂无采样";
    public string? CpuHint => Cpu == "—" ? MissingValueHint : null;
    public string? MemoryHint => Memory == "—" ? MissingValueHint : null;
    public string? UptimeHint => Uptime == "—" ? MissingValueHint : null;

    public void Update(ScriptUsage? usage)
    {
        Cpu = DashboardViewModel.Format(usage?.Cpu, "0.0", "%");
        Memory = DashboardViewModel.Format(usage?.MemoryMiB, "0", " MiB");
        Uptime = usage?.Uptime is { } uptime ? $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}" : "—";
    }
}

internal sealed partial class ScriptsViewModel : ObservableObject
{
    [ObservableProperty] private ScriptItem? _selected;

    public ScriptsViewModel(IDesktopData data)
    {
        Items = data.Scripts;
        _selected = Items.FirstOrDefault();
        CanManage = data is LiveDesktopData;
        if (data is LiveDesktopData live) live.Updated += (_, _) => UpdateItems(live.Scripts);
    }

    public IReadOnlyList<ScriptItem> Items { get; private set; }
    public bool HasItems => Items.Count > 0;
    public bool CanManage { get; }
    public string CountLabel => $"{Items.Count} 个软件";
    public string DiscoveryHint => CanManage ? "检测桌面和开始菜单中的已知软件快捷方式。" : "演示数据仅供预览。";

    private void UpdateItems(IReadOnlyList<ScriptItem> items)
    {
        if (ReferenceEquals(Items, items)) return;
        var selection = Selected?.Installation?.Id;
        Items = items;
        OnPropertyChanged(nameof(Items));
        Selected = Items.FirstOrDefault(item => item.Installation?.Id == selection) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountLabel));
    }
}
