using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using BAM.Adapters;
using BAM.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BAM.Desktop.ViewModels;

internal sealed partial class MaaLoopbackViewModel : ObservableObject
{
    private readonly IMaaLoopbackHost _host;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MaaHeartbeatSession? _session;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(ProbeCommand))]
    private bool _listening;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(ProbeCommand))]
    private bool _busy;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ProbeCommand))]
    private bool _awaitingReport;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GetTaskAddress), nameof(ReportStatusAddress))]
    private string _portText = "18373";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GetTaskAddress), nameof(ReportStatusAddress))]
    private int? _boundPort;
    [ObservableProperty] private string _status = "已停止。";
    [ObservableProperty] private string _pollStatus = "";
    [ObservableProperty] private string _probeStatus = "";

    public MaaLoopbackViewModel(IMaaLoopbackHost host, TimeProvider clock, CancellationToken lifetime)
    {
        (_host, _clock, _lifetime) = (host, clock, lifetime);
        _timer.Tick += (_, _) => Refresh();
    }

    public bool CanStart => !Busy && !Listening;
    public string GetTaskAddress => Address(MaaLoopbackRoutes.GetTask);
    public string ReportStatusAddress => Address(MaaLoopbackRoutes.ReportStatus);
    private bool CanStop() => !Busy && Listening;
    private bool CanProbe() => CanStop() && !AwaitingReport;

    private string Address(string route) => (BoundPort ?? ParsePort(PortText)) is { } port
        ? FormattableString.Invariant($"http://127.0.0.1:{port}{route}") : "";

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1024 and <= 65535
            ? port : null;

    public async Task StartAsync(string user, string device)
    {
        if (!CanStart || _lifetime.IsCancellationRequested) return;
        if (ParsePort(PortText) is not { } port)
        {
            Status = "请输入 1024–65535 的端口号。";
            return;
        }
        Busy = true;
        try
        {
            var session = new MaaHeartbeatSession(user, device, _clock);
            var boundPort = await _host.StartAsync(port, session, _lifetime);
            if (_lifetime.IsCancellationRequested) return;
            _session = session;
            BoundPort = boundPort;
            Status = "已监听本机 IPv4 回环。";
            Listening = true;
            Refresh();
            _timer.Start();
        }
        catch (ArgumentException) { Status = "请填写有效的配对标识，每项 1–128 字符。"; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "无法启动回环服务。请检查端口占用，或换一个端口。";
        }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private void Probe()
    {
        _session?.TryQueueHeartbeat();
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        Busy = true;
        try
        {
            await ShutdownAsync();
            Status = "已停止。";
        }
        finally { Busy = false; }
    }

    public Task ShutdownAsync()
    {
        _timer.Stop();
        _session = null;
        Listening = false;
        AwaitingReport = false;
        BoundPort = null;
        PollStatus = ProbeStatus = "";
        return _host.StopAsync(CancellationToken.None);
    }

    private void Refresh()
    {
        if (_session is null) return;
        var snapshot = _session.Snapshot();
        PollStatus = snapshot.LastPoll is { } poll ? $"最近配对轮询 {poll.ToLocalTime():HH:mm:ss}" : "等待配对的 MAA 轮询。";
        AwaitingReport = snapshot.State is RemoteCommandState.Queued or RemoteCommandState.Offered;
        ProbeStatus = snapshot.State switch
        {
            RemoteCommandState.Queued => "心跳已排队，等待轮询。",
            RemoteCommandState.Offered => "已提供心跳，等待 MAA 回报。",
            RemoteCommandState.TimedOut => "等待超时，结果未知。",
            RemoteCommandState.Reported when snapshot.ReportSucceeded != true => "已收到心跳失败回报。",
            RemoteCommandState.Reported when snapshot.ReportContainsRemoteTask == true => "已收到心跳，返回了远程顺序任务标识。",
            RemoteCommandState.Reported => "已收到心跳，回报未提供远程顺序任务标识。",
            _ => "尚未发起心跳。"
        };
    }
}
