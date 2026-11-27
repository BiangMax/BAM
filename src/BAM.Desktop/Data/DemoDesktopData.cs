#if BAM_DEMO
using BAM.Core;
using Wpf.Ui.Controls;

namespace BAM.Desktop.Data;

internal sealed class DemoDesktopData : IDesktopData
{
    private readonly DateTimeOffset _started;
    public bool IsDemo => true;
    public bool HasResourceSource => true;
    public IReadOnlyList<ScriptItem> Scripts { get; } =
    [
        new("MAA", "明日方舟", "v5.0.0", SymbolRegular.Apps24, Tone.Success,
            ProcessState.Running, GameTaskState.Unknown,
            @"<示例目录>\MAA\MAA.exe", @"<示例目录>\MAA", "—",
            "软件发现、窗口激活、进程管理、任务检测均待接入。"),
        new("MaaEnd", "明日方舟：终末地", "v1.2.0", SymbolRegular.Cube24, Tone.Accent,
            ProcessState.Running, GameTaskState.Unknown,
            @"<示例目录>\MaaEnd\MaaEnd.exe", @"<示例目录>\MaaEnd", "—",
            "软件发现、窗口激活、进程管理、任务检测均待接入。"),
        new("BBchannel", "Fate/Grand Order", "v2.3.0", SymbolRegular.Window24, Tone.Warning,
            ProcessState.Stopped, GameTaskState.Unsupported,
            @"<示例目录>\BBchannel\BBchannel.exe", @"<示例目录>\BBchannel", "—",
            "通用进程管理待接入。任务检测不支持。")
    ];
    public IReadOnlyList<LogEntry> Logs { get; }

    public DemoDesktopData()
    {
        _started = DateTimeOffset.UtcNow;
        Logs =
        [
            new(_started.AddSeconds(-3), "BAM", LogLevel.Information, "已加载演示工作区", "本次会话使用合成样本。"),
            new(_started.AddSeconds(-27), "MaaEnd", LogLevel.Information, "资源采样已恢复", "演示时间序列保留了缺失采样的间隔。"),
            new(_started.AddSeconds(-35), "MAA", LogLevel.Warning, "等待可验证的任务状态", "当前样例仅有进程信息，任务状态保持未知。"),
            new(_started.AddSeconds(-62), "BBchannel", LogLevel.Error, "日志来源暂不可用", "演示故障：日志来源未连接，列表保留历史样本。"),
            new(_started.AddMinutes(-2), "MaaEnd", LogLevel.Information, "已获得软件状态快照", "样例进程为已启动。"),
            new(_started.AddMinutes(-3), "MAA", LogLevel.Information, "已获得私有工作集样本", "样例资源范围为软件主进程。"),
            new(_started.AddMinutes(-4), "BBchannel", LogLevel.Information, "软件状态已更新", "样例进程为已停止。"),
            new(_started.AddMinutes(-6), "BAM", LogLevel.Information, "已读取演示脚本清单", "演示清单包含 3 个软件。")
        ];
    }

    public string? GetLogSourceNotice(string source)
        => source == "BBchannel" ? "演示故障 · 日志来源暂不可用，仍可查看历史样本。" : null;

    public IReadOnlyList<ResourceFrame> ReadRecentResources(DateTimeOffset time)
        => Enumerable.Range(-ResourceHistory.WindowSeconds, ResourceHistory.WindowSeconds)
            .Select(second => ReadResources(time.AddSeconds(second))).ToArray();

    public ResourceFrame ReadResources(DateTimeOffset time)
    {
        var t = (time - _started).TotalSeconds;
        var missing = t is >= -33 and <= -28;
        var maa = Usage(2.7, 168, 21);
        var maaEnd = Usage(0.6, 244, 46);
        return new(time, [maa, maaEnd, new(null, null, null)],
            maa.Cpu + maaEnd.Cpu, maa.MemoryMiB + maaEnd.MemoryMiB);

        ScriptUsage Usage(double phase, int memory, int minutes)
        {
            var burst = Math.Pow((1 + Math.Sin(t * 0.081 + 0.27)) / 2, 3);
            double? cpu = missing ? null : 6 + 17 * burst + 2.8 * Math.Sin(t * 0.153 + phase)
                + 1.4 * Math.Sin(t * 0.36 + phase) + 0.6 * Math.Cos(t * 0.77);
            double? workingSet = missing ? null : memory + 9 * Math.Sin(t * 0.043 + phase) + 3 * Math.Sin(t * 0.23);
            return new(cpu, workingSet, TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(t));
        }
    }
}
#endif
