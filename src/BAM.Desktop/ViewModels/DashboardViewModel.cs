using BAM.Core;
using BAM.Desktop.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace BAM.Desktop.ViewModels;

internal sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IDesktopData _data;
    private DateTimeOffset? _lastSample;

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
    }

    public ResourceHistory History { get; } = new();
    public bool IsDemo => _data.IsDemo;
    public bool HasResourceSource => _data.HasResourceSource;
    public IReadOnlyList<ScriptCardViewModel> Cards { get; }
    public IReadOnlyList<LogEntry> RecentEvents { get; }
    public bool HasScripts => Cards.Count > 0;
    public bool HasEvents => RecentEvents.Count > 0;
    public string ProcessCount => HasResourceSource ? $"{_data.Scripts.Count(x => x.Process == ProcessState.Running)} / {Cards.Count}" : $"— / {Cards.Count}";
    public string ChartScope => IsDemo ? "演示脚本主进程" : "已确认脚本主进程";
    public string ResourceHint => HasResourceSource ? ChartScope : "等待接入进程采样";
    public bool ShowCpu
    {
        get => !ShowMemory;
        set { if (value) ShowMemory = false; }
    }

    partial void OnShowMemoryChanged(bool value) => OnPropertyChanged(nameof(ShowCpu));

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
    public ScriptItem Script { get; } = script;
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
    }

    public IReadOnlyList<ScriptItem> Items { get; }
    public bool HasItems => Items.Count > 0;
    public string CountLabel => $"{Items.Count} 个软件";
    public string DiscoveryHint => "安装发现与定位尚未接入";
}
