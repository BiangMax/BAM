using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using BAM.Adapters;
using BAM.Core;
using Wpf.Ui.Controls;

namespace BAM.Desktop.Data;

internal sealed class LiveDesktopData(ProgramMonitor monitor, InstallationCatalog catalog,
    MaaEndTaskObserver? maaEndTasks = null) : IDesktopData, IDisposable
{
    private readonly ConditionalWeakTable<byte[], BitmapSource> _icons = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _changes = [];
    private Task _monitor = Task.CompletedTask;
    private MonitorOptions _options = new(true, TimeSpan.FromSeconds(1));
    private IReadOnlyList<ProgramEvent>? _previousEvents;
    private long _noticeRevision;
    private LaunchNotice? _launchNotice;

    public event EventHandler? Updated;
    public bool IsDemo => false;
    public IReadOnlyList<ScriptItem> Scripts { get; private set; } = [];
    public IReadOnlyList<LogEntry> Logs { get; private set; } = [];
    public bool HasResourceSource => true;
    public ProgramFrame? Frame { get; private set; }
    public bool RedactDetails { get; set; }
    public string? Notice { get; private set; }
    public string? GetLogSourceNotice(string source) => null;
    public ResourceFrame? ReadResources(DateTimeOffset time) => null;

    public void Configure(bool collect, TimeSpan delay)
    {
        var current = new MonitorOptions(collect, delay);
        if (current == _options) return;
        Volatile.Write(ref _options, current);
        Wake();
    }

    public void Start(Func<Action, Task> dispatch) => _monitor = Task.Run(async () =>
    {
        var token = _stop.Token;
        try
        {
            await monitor.InitializeAsync(token).ConfigureAwait(false);
            try { await monitor.DiscoverAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (IsExpected(error))
            {
                await dispatch(() => SetNotice("自动发现结果无法保存，请在脚本管理中重试。")).ConfigureAwait(false);
            }
            while (!token.IsCancellationRequested)
            {
                var options = Volatile.Read(ref _options);
                var frame = await monitor.SampleAsync(options.Collect, token).ConfigureAwait(false);
                if (maaEndTasks is not null)
                    frame = await maaEndTasks.ObserveAsync(frame, token).ConfigureAwait(false);
                var items = frame.Programs.Select(ToScript).ToArray();
                var events = monitor.Events;
                await dispatch(() => Publish(frame, items, events)).ConfigureAwait(false);
                await _wake.WaitAsync(options.Delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsExpected(error))
        {
            await dispatch(() => SetNotice("实时监控无法继续，请检查本地文件权限后重新打开 BAM。")).ConfigureAwait(false);
        }
    });

    private void Publish(ProgramFrame frame, IReadOnlyList<ScriptItem> scripts, IReadOnlyList<ProgramEvent> events)
    {
        if (_stop.IsCancellationRequested) return;
        Frame = frame;
        if (_launchNotice is { } pending && ResolveLaunchNotice(frame, pending.Program, pending.PreviousWorker,
            pending.PreviousFrame) is { } outcome)
        {
            _launchNotice = null;
            Notice = outcome;
        }
        if (!Scripts.SequenceEqual(scripts)) Scripts = scripts;
        if (!ReferenceEquals(_previousEvents, events))
        {
            _previousEvents = events;
            Logs = events.Select(entry => new LogEntry(entry.Time, entry.Source, (LogLevel)entry.Severity,
                entry.Message, entry.Detail) { InstallationId = entry.InstallationId }).ToArray();
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public Task DiscoverAsync() => Track(async token =>
    {
        var count = await monitor.DiscoverAsync(token);
        SetNotice(count > 0 ? $"已添加 {count} 个新安装。" : "没有发现新安装。");
    });

    public Task RemoveAsync(Guid id) => Track(async token =>
    {
        await monitor.RemoveAsync(id, token);
        SetNotice("已移除管理记录。");
    });

    public Task ExecuteAsync(ScriptItem script, ProgramAction action) => Track(async token =>
    {
        if (script.Installation is not { } program) return;
        var previousWorker = script.Identity ?? Frame?.Programs
            .FirstOrDefault(item => ReferenceEquals(item.Installation, program))?.Worker?.Identity;
        var pending = new LaunchNotice(program, previousWorker, Frame);
        SetNotice($"{script.Name} · 正在处理操作");
        var revision = _noticeRevision;
        var result = await Task.Run(() => monitor.ExecuteAsync(program, action, script.Identity, script.WindowHandle, token), token);
        if (revision != _noticeRevision || token.IsCancellationRequested) return;
        if (result.Status == CommandStatus.Accepted && action is ProgramAction.Start or ProgramAction.Restart or ProgramAction.StartWithEmulator)
        {
            if (ResolveLaunchNotice(Frame, program, pending.PreviousWorker, pending.PreviousFrame) is { } outcome)
                SetNotice(outcome);
            else SetNotice($"{script.Name} · {result.Reason ?? "等待确认工作进程。"}", pending);
            return;
        }
        SetNotice($"{script.Name} · {result.Reason ?? "操作结果未知。"}");
    });

    public Task<ProgramInstallation> ImportAsync(string path) => Task.Run(() => catalog.Import(path), _stop.Token);
    public Task<LaunchEntry> ImportEmulatorAsync(string path) => Task.Run(() => catalog.ImportEmulator(path), _stop.Token);
    public Task<string> ImportEmulatorImageAsync(string path) => Task.Run(() => catalog.ImportEmulatorImage(path), _stop.Token);

    public Task SaveAsync(ProgramInstallation original, string name, string directory, string arguments, string? worker,
        EmulatorStartup? emulator, EmulatorMonitoring? monitoring) =>
        Track(async token =>
        {
            var program = await Task.Run(() => catalog.Edit(original with { Emulator = emulator, EmulatorMonitor = monitoring },
                name, directory, arguments, worker), token);
            await monitor.SaveAsync(program, token);
            SetNotice($"已保存 {program.Name} 的管理配置。");
        }, report: false);

    private async Task Track(Func<CancellationToken, Task> work, bool report = true)
    {
        if (_stop.IsCancellationRequested) return;
        var task = work(_stop.Token);
        _changes.Add(task);
        try { await task; }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (report && IsExpected(error))
        {
            SetNotice(error is IOException or UnauthorizedAccessException ? "本地操作失败，请检查安装文件及配置目录的权限。" : error.Message);
        }
        finally
        {
            _changes.Remove(task);
            Wake();
        }
    }

    private void SetNotice(string notice, LaunchNotice? launch = null)
    {
        if (_stop.IsCancellationRequested) return;
        _noticeRevision++;
        _launchNotice = launch;
        Notice = notice;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    internal static string? ResolveLaunchNotice(ProgramFrame? frame, ProgramInstallation program,
        ProcessIdentity? previousWorker, ProgramFrame? previousFrame)
    {
        if (frame is null || ReferenceEquals(frame, previousFrame)) return null;
        var status = frame.Programs.FirstOrDefault(item => ReferenceEquals(item.Installation, program));
        if (status is { Process: ProcessState.Running, Worker: { } worker } && !worker.Identity.Equals(previousWorker))
            return $"{program.Name} · 已确认软件运行。";
        return status is { LaunchConfirmationTimedOut: true }
            ? $"{program.Name} · 启动后未能确认工作进程。请在外部核对。"
            : null;
    }

    private sealed record LaunchNotice(ProgramInstallation Program, ProcessIdentity? PreviousWorker, ProgramFrame? PreviousFrame);

    private void Wake()
    {
        if (!_stop.IsCancellationRequested && _wake.CurrentCount == 0) _wake.Release();
    }

    public Task StopAsync()
    {
        _stop.Cancel();
        return Task.WhenAll(_changes.Append(_monitor).Select(task =>
            task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
    }

    public void Dispose()
    {
        _stop.Dispose();
        _wake.Dispose();
    }

    private ScriptItem ToScript(ProgramStatus status)
    {
        var program = status.Installation;
        var (game, symbol, tone) = program.Kind switch
        {
            ProgramKind.MaaEnd => ("明日方舟：终末地", SymbolRegular.Apps24, Tone.Accent),
            ProgramKind.Maa => ("明日方舟", SymbolRegular.Apps24, Tone.Success),
            ProgramKind.BBchannel => ("Fate/Grand Order", SymbolRegular.Apps24, Tone.Warning),
            _ => ("Windows 程序", SymbolRegular.Apps24, Tone.Neutral)
        };
        var window = status.Worker?.Window;
        var windowDetail = window is null ? status.Worker is null ? "窗口状态未知" : "无可管理窗口"
            : $"{(window.State == WindowState.Minimized ? "已最小化" : "可见")} · {(window.Responding ? "有响应" : "无响应")}" +
                (RedactDetails ? "" : $" · {window.Title}");
        var actions = status.Actions;
        var available = new[] { (ProgramAction.Start, "启动"), (ProgramAction.Activate, "打开窗口"),
            (ProgramAction.Close, "正常关闭"), (ProgramAction.Restart, "重启"), (ProgramAction.Force, "强制结束") }
            .Where(action => actions.Contains(action.Item1)).Select(action => action.Item2).ToArray();
        return new(program.Name, game, status.Metadata.Version, symbol, tone, status.Process, status.Task,
            program.InstallDirectory, program.Entry.WorkingDirectory, program.Entry.Arguments.Length == 0 ? "—" : program.Entry.Arguments,
            $"{windowDetail}\n{(available.Length == 0 ? "暂无可用控制操作" : string.Join("、", available))} · {status.TaskHint ?? "未接入任务检测"}")
        {
            Installation = program,
            ActualIcon = status.Metadata.Icon is { } bytes ? _icons.GetValue(bytes, DecodeIcon) : null,
            VersionDetail = status.Metadata.VersionDetail,
            Issue = status.Issue,
            TaskHint = status.TaskHint,
            Identity = status.Worker?.Identity,
            WindowHandle = window?.Handle ?? 0,
            CanStart = actions.Contains(ProgramAction.Start),
            CanActivate = actions.Contains(ProgramAction.Activate),
            CanClose = actions.Contains(ProgramAction.Close),
            CanRestart = actions.Contains(ProgramAction.Restart),
            CanForce = actions.Contains(ProgramAction.Force),
            NeedsRelocation = !status.Metadata.EntryAvailable,
            EmulatorUsage = status.EmulatorUsage,
            RedactDetails = RedactDetails
        };
    }

    private static BitmapSource DecodeIcon(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException;

    private sealed record MonitorOptions(bool Collect, TimeSpan Delay);
}
