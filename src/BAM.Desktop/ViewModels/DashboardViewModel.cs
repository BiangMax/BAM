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

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChartSamples))] private bool _showMemory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChartSamples))] private int _chartRevision;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DetectionTitle), nameof(DetectionDescription), nameof(ProcessCount))]
    private bool _isDetecting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DetectionTitle), nameof(DetectionDescription), nameof(ProcessCount))]
    private string? _detectionIssue;
    [ObservableProperty] private string _sampleLabel = "等待采样";
    [ObservableProperty] private string _cpu = "—";
    [ObservableProperty] private string _memory = "—";
    [ObservableProperty] private string _chartDetail = "暂无资源样本";

    public DashboardViewModel(IDesktopData data)
    {
        _data = data;
        _isDetecting = data is LiveDesktopData { Frame: null, DetectionIssue: null };
        Cards = data.Scripts.OrderBy(CardPriority).Select(script => new ScriptCardViewModel(script)).ToArray();
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
    public bool CanManage => _data is LiveDesktopData;
    public string DetectionTitle => DetectionIssue is not null ? "未能确认脚本列表" : IsDetecting ? "正在检测脚本" : "尚未添加脚本";
    public string DetectionDescription => DetectionIssue ?? (IsDetecting
        ? "正在读取本地记录并查找已知软件。" : CanManage ? "可自动检测已知软件，或手动添加。" : "当前没有脚本记录。");
    public bool HasChartSamples => _lastFrame?.ResourcesPaused != true && (ShowMemory ? History.HasMemory : History.HasCpu);
    private (string Title, string Hint) ChartEmptyContent => _lastFrame switch
    {
        { ResourcesPaused: true } => ("资源采样已暂停", "可在设置中重新开启资源采样。"),
        _ when IsDetecting => ("正在检测脚本", "完成检测后会自动开始采样。"),
        _ when DetectionIssue is not null && !HasScripts => ("暂时无法读取脚本列表", "可在脚本管理中重新检测。"),
        _ when !HasResourceSource => ("资源监控不可用", "当前会话没有资源数据源。"),
        _ when !HasScripts => ("还没有可监控的脚本", "添加脚本后即可监控资源占用。"),
        { ResourceProcessCount: 0, UnconfirmedResourceTargets: 0 } => ("没有运行中的脚本或模拟器", "启动脚本或已绑定的模拟器后，曲线会自动显示。"),
        { UnconfirmedResourceTargets: > 0 } => ("正在确认监控对象", "确认脚本和已绑定的模拟器后，曲线会自动显示。"),
        _ => ("等待占用数据", "获取完整的采样后，曲线会自动显示。")
    };
    public string ChartEmptyTitle => ChartEmptyContent.Title;
    public string ChartEmptyHint => ChartEmptyContent.Hint;
    public bool HasEvents => RecentEvents.Count > 0;
    public string ProcessCount
    {
        get
        {
            if (_data is LiveDesktopData live)
            {
                if (IsDetecting || (DetectionIssue is not null && !HasScripts)) return "— / —";
                return $"{live.Frame?.ConfirmedWorkers ?? 0} / {Cards.Count}";
            }
            return HasResourceSource ? $"{_data.Scripts.Count(x => x.Process == ProcessState.Running)} / {Cards.Count}" : $"— / {Cards.Count}";
        }
    }
    public string ChartScope => IsDemo ? "演示脚本资源" : "脚本及对应模拟器";
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
    private string CoverageHint(int? covered) => _lastFrame switch
    {
        { ResourcesPaused: true } => "资源采样已暂停",
        { UnconfirmedResourceTargets: > 0 } frame => covered is > 0
            ? $"部分采样 · {frame.UnconfirmedResourceTargets} 项待确认" : $"{frame.UnconfirmedResourceTargets} 项待确认",
        { ResourceProcessCount: > 0 } frame when covered < frame.ResourceProcessCount => $"已采样 {covered}/{frame.ResourceProcessCount} 个进程",
        _ => HasResourceSource ? ChartScope : "等待接入进程采样"
    };
    public bool ShowCpu
    {
        get => !ShowMemory;
        set { if (value) ShowMemory = false; }
    }

    partial void OnShowMemoryChanged(bool value) => OnPropertyChanged(nameof(ShowCpu));

    private static int CardPriority(ScriptItem script) => script.Installation?.Kind switch
    {
        ProgramKind.Maa => 0,
        ProgramKind.MaaEnd => 1,
        _ => 2
    };

    private void UpdateLive(LiveDesktopData live)
    {
        DetectionIssue = live.DetectionIssue;
        IsDetecting = live.Frame is null && DetectionIssue is null;
        if (live.Frame is not { } frame || ReferenceEquals(frame, _lastFrame))
        {
            OnPropertyChanged(nameof(ChartEmptyTitle));
            OnPropertyChanged(nameof(ChartEmptyHint));
            return;
        }
        _lastFrame = frame;
        var cards = Cards.ToDictionary(card => card.Script.Installation!.Id);
        var next = new List<ScriptCardViewModel>();
        foreach (var (item, status) in live.Scripts.Zip(frame.Programs).OrderBy(pair => CardPriority(pair.First)))
        {
            if (!cards.TryGetValue(item.Installation!.Id, out var card)) card = new(item);
            card.Script = item;
            card.Update(new(status.Cpu, status.PrivateWorkingSetMiB, status.Uptime));
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
        OnPropertyChanged(nameof(ChartEmptyTitle));
        OnPropertyChanged(nameof(ChartEmptyHint));
        var hadData = History.HasData;
        var resolved = frame.UnconfirmedResourceTargets == 0;
        History.Add(new(frame.ObservedAt,
            resolved && frame.CpuCoverage == frame.ResourceProcessCount ? frame.Cpu : null,
            resolved && frame.MemoryCoverage == frame.ResourceProcessCount ? frame.PrivateWorkingSetMiB : null, frame.Elapsed));
        Cpu = Format(frame.Cpu, "0.0", "%");
        Memory = Format(frame.PrivateWorkingSetMiB, "0", " MiB");
        if (frame.Cpu is not null || frame.PrivateWorkingSetMiB is not null) _lastSample = frame.ObservedAt;
        SampleLabel = _lastSample is { } latest ? $"最近有效采样 {latest.ToLocalTime():HH:mm:ss}" : "暂无有效采样";
        ChartDetail = $"最近 120 秒 · CPU {frame.CpuCoverage}/{frame.ResourceProcessCount} · 内存 {frame.MemoryCoverage}/{frame.ResourceProcessCount}" +
            (resolved ? "" : $" · {frame.UnconfirmedResourceTargets} 项待确认");
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
        ChartDetail = $"最近 120 秒 · {SampleLabel} · CPU {Cpu} · 内存 {Memory}";
        ChartRevision++;
    }

    internal static string Format(double? value, string format, string unit)
        => value is double number ? $"{number.ToString(format)}{unit}" : "—";
}

internal sealed partial class ScriptCardViewModel(ScriptItem script) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CpuHint), nameof(MemoryHint), nameof(UptimeHint), nameof(EmulatorCpu), nameof(EmulatorMemory), nameof(CanPrimary), nameof(PrimaryLabel))]
    private ScriptItem _script = script;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrimary))] private bool _pairEnabled;
    public string PrimaryLabel => Script.IsPairedStarting ? Script.EmulatorAction : Script.PrimaryAction;
    public bool CanPrimary => !Script.IsPairedStarting && Script.CanPrimary &&
        (!PairEnabled || Script.Installation?.Emulator is null || !Script.CanStart || Script.CanStartWithEmulator);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CpuHint))] private string _cpu = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MemoryHint))] private string _memory = "—";
    [ObservableProperty] private string _combinedMemory = "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UptimeHint))] private string _uptime = "—";
    private string MissingValueHint => Script.Process == ProcessState.Stopped ? "软件已停止" : "暂无采样";
    public string? CpuHint => Cpu == "—" ? MissingValueHint : null;
    public string? MemoryHint => Memory == "—" ? MissingValueHint : null;
    public string? UptimeHint => Uptime == "—" ? MissingValueHint : null;
    public string EmulatorCpu => DashboardViewModel.Format(Script.EmulatorUsage?.Cpu, "0.0", "%");
    public string EmulatorMemory => DashboardViewModel.Format(Script.EmulatorUsage?.PrivateWorkingSetMiB, "0", " MiB");

    public void Update(ScriptUsage? usage)
    {
        Cpu = DashboardViewModel.Format(usage?.Cpu, "0.0", "%");
        Memory = DashboardViewModel.Format(usage?.MemoryMiB, "0", " MiB");
        var scriptMemory = usage?.MemoryMiB ?? (Script.Process == ProcessState.Stopped ? 0d : (double?)null);
        CombinedMemory = DashboardViewModel.Format(scriptMemory + Script.EmulatorUsage?.PrivateWorkingSetMiB, "0", " MiB");
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
