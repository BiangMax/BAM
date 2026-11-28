using BAM.Adapters;
using BAM.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BAM.Desktop.Data;

internal sealed class BatchReport(IReadOnlyList<BatchLaunchResult> results)
{
    public string Summary { get; } = $"批量启动 · 已提交 {results.Count(row => row.Result.Status == CommandStatus.Accepted)}/{results.Count} 组";
    public IReadOnlyList<BatchReportRow> Rows { get; } = results.Select(result => new BatchReportRow(result)).ToArray();

    /// <summary>有组未能确认或明确失败时，结果需要用户留意。</summary>
    public bool NeedsAttention => Rows.Any(row => row.NeedsAttention);

    /// <summary>所有组都已离开待确认阶段，可以给出结果。</summary>
    public bool IsSettled => Rows.All(row => row.IsSettled);

    public void Refresh()
    {
        foreach (var row in Rows) row.Refresh();
    }
}

internal sealed class BatchReportRow(BatchLaunchResult result) : ObservableObject
{
    private BatchLaunchState _state = result.State;
    public string Name => result.Program.Name;
    public bool NeedsAttention => _state is BatchLaunchState.Unconfirmed or BatchLaunchState.Failed;
    public bool IsSettled => _state is not BatchLaunchState.Pending;
    public string Status => _state switch
    {
        BatchLaunchState.Pending => "已提交待确认",
        BatchLaunchState.Running => "已确认运行",
        BatchLaunchState.Unconfirmed => "未能确认",
        BatchLaunchState.Skipped => "已跳过",
        _ => "明确失败"
    };
    public string Detail => _state switch
    {
        BatchLaunchState.Pending => "Windows 已接收软件启动请求，正在等待工作进程。",
        BatchLaunchState.Running => "已观测到本轮启动的新工作进程。",
        BatchLaunchState.Unconfirmed when result.Result.Status == CommandStatus.Accepted =>
            "已提交启动请求，未能确认新的工作进程。请核对脚本。",
        _ => result.Result.Reason ?? "请核对对应脚本及启动入口。"
    };

    public void Refresh()
    {
        var state = result.State;
        if (state == _state) return;
        _state = state;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Detail));
    }
}
