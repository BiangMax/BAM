using BAM.Core;

namespace BAM.Adapters;

public sealed class ProgramMonitor(InstallationCatalog catalog, IProgramPlatform platform, IProgramStore store,
    IProgramEventStore eventStore, TimeProvider clock) : IDisposable
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly long _started = clock.GetTimestamp();
    private readonly EmulatorResources _emulators = new(platform, clock);
    private readonly List<ProgramInstallation> _programs = [];
    private readonly HashSet<string> _suppressedPaths = new(Paths);
    private readonly Dictionary<Guid, ProgramMetadata> _metadata = [];
    private readonly Dictionary<Guid, ProgramStatus> _previous = [];
    private readonly Dictionary<ProcessIdentity, (long Time, TimeSpan Cpu)> _cpu = [];
    private readonly Dictionary<ProcessIdentity, uint?> _exits = [];
    private readonly Dictionary<Guid, PendingLaunch> _launches = [];
    private readonly HashSet<Guid> _emulatorSubmitted = [];
    private readonly Dictionary<Guid, (long StartedAt, TimeSpan Duration)> _emulatorWaits = [];
    private readonly Dictionary<Guid, ProgramAction> _busy = [];
    private readonly HashSet<ProcessIdentity> _expectedExits = [];
    private readonly Dictionary<ProcessIdentity, bool> _awaitingExit = [];
    private readonly ProgramEventBuffer _events = new();
    private long? _metadataAt;
    private bool _eventStoreUnavailable;
    private bool _initialized;
    private int _batchActive;
    public string? InstallationReadIssue { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            try { _events.Restore(await eventStore.LoadAsync(cancellation).ConfigureAwait(false)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AddLogFailure(); }
            var (saved, suppressedPaths, notice, readable) = await store.LoadAsync(cancellation).ConfigureAwait(false);
            if (!readable) InstallationReadIssue = notice ?? "无法读取安装配置。";
            _suppressedPaths.UnionWith(suppressedPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
            foreach (var program in saved)
            {
                try
                {
                    catalog.Validate(program, requireFiles: false);
                    if (_programs.Any(existing => existing.Id == program.Id || InstallationCatalog.Overlaps(existing, program)))
                        throw new ArgumentException();
                    _programs.Add(program);
                }
                catch (Exception error) when (error is ArgumentException or NullReferenceException)
                {
                    notice = "部分安装记录格式无效或指向同一工作程序，已跳过。";
                    InstallationReadIssue ??= notice;
                }
            }
            if (notice is not null) await WriteEvent(null, EventSeverity.Warning, notice, "", cancellation).ConfigureAwait(false);
            await WriteEvent(null, EventSeverity.Information, "BAM 已启动", "已启用本地安装与进程观测。", cancellation).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public Task<int> DiscoverAsync(CancellationToken cancellation) => ChangeAsync(async () =>
    {
        var next = _programs.ToList();
        var added = catalog.Discover(cancellation).Where(candidate =>
            !next.Any(existing => InstallationCatalog.Overlaps(existing, candidate)) &&
            !_suppressedPaths.Contains(candidate.Entry.OriginalPath) &&
            !candidate.WorkerPaths.Any(_suppressedPaths.Contains)).ToArray();
        if (added.Length == 0) return 0;
        next.AddRange(added);
        await Save(next, _suppressedPaths.ToArray(), cancellation).ConfigureAwait(false);
        foreach (var program in added)
            await WriteEvent(program, EventSeverity.Information, "发现软件", "已从 Windows 快捷方式添加安装。", cancellation).ConfigureAwait(false);
        return added.Length;
    }, cancellation);

    public Task SaveAsync(ProgramInstallation program, CancellationToken cancellation) => ChangeAsync(async () =>
    {
        catalog.Validate(program, requireFiles: true);
        if (_busy.ContainsKey(program.Id) || _launches.ContainsKey(program.Id))
            throw new InvalidOperationException("启动状态尚未确认，暂时无法修改此记录。");
        if (_programs.Any(existing => existing.Id != program.Id && InstallationCatalog.Overlaps(existing, program)))
            throw new ArgumentException("此工作程序已有管理记录，请在现有记录中修改。");
        var next = _programs.ToList();
        var index = next.FindIndex(existing => existing.Id == program.Id);
        var currentEmulator = index >= 0 ? next[index].Emulator : null;
        if (program.Emulator is { } setup && (index < 0 || next[index].Emulator != setup) &&
            catalog.EmulatorIssue(program, setup) is { } issue)
            throw new ArgumentException(issue);
        if (program.EmulatorMonitor is { } monitoring && (index < 0 || next[index].EmulatorMonitor != monitoring))
            InstallationCatalog.ValidateEmulatorMonitoring(monitoring);
        if (index < 0) next.Add(program);
        else next[index] = program;
        var suppression = _suppressedPaths.Except(program.WorkerPaths.Append(program.Entry.OriginalPath), Paths).ToArray();
        await Save(next, suppression, cancellation).ConfigureAwait(false);
        if (currentEmulator?.Entry is { } prior && (program.Emulator?.Entry is not { } changed || LauncherKey(prior) != LauncherKey(changed)))
            _emulatorSubmitted.Remove(program.Id);
        _metadata.Remove(program.Id);
        _previous.Remove(program.Id);
        await WriteEvent(program, EventSeverity.Information, index < 0 ? "已添加管理记录" : "已修改管理记录", "", cancellation)
            .ConfigureAwait(false);
        return true;
    }, cancellation);

    public Task RemoveAsync(Guid id, CancellationToken cancellation) => ChangeAsync(async () =>
    {
        if (_busy.ContainsKey(id) || _launches.ContainsKey(id))
            throw new InvalidOperationException("启动状态尚未确认，暂时无法移除此记录。");
        var program = _programs.Find(item => item.Id == id);
        if (program is null) return false;
        var suppression = _suppressedPaths.Union(program.WorkerPaths.Append(program.Entry.OriginalPath), Paths).ToArray();
        await Save(_programs.Where(item => item.Id != id).ToArray(), suppression, cancellation).ConfigureAwait(false);
        _emulatorSubmitted.Remove(id);
        _metadata.Remove(id);
        _previous.Remove(id);
        await WriteEvent(program, EventSeverity.Information, "已移除管理记录", "", cancellation).ConfigureAwait(false);
        return true;
    }, cancellation);

    private Task<T> ChangeAsync<T>(Func<Task<T>> change, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            await InitializeAsync(cancellation).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await change().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }, cancellation);

    private async Task Save(IReadOnlyList<ProgramInstallation> programs, string[] suppression, CancellationToken cancellation)
    {
        await store.SaveAsync(programs, suppression, cancellation).ConfigureAwait(false);
        InstallationReadIssue = null;
        _programs.Clear();
        _programs.AddRange(programs);
        _suppressedPaths.Clear();
        _suppressedPaths.UnionWith(suppression);
    }

    public async Task<ProgramFrame> SampleAsync(bool resources, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var stamp = clock.GetTimestamp();
            var now = clock.GetUtcNow();
            bool refreshMetadata = _metadataAt is null || clock.GetElapsedTime(_metadataAt.Value, stamp) >= TimeSpan.FromSeconds(30);
            if (refreshMetadata) _metadataAt = stamp;
            var scan = Scan(resources, cancellation);
            var emulatorUsage = _emulators.Sample(_programs, resources, stamp, cancellation);
            List<ProgramStatus> statuses = [];
            Dictionary<ProcessIdentity, (long Time, TimeSpan Cpu)> nextCpu = [];
            foreach (var program in _programs)
            {
                cancellation.ThrowIfCancellationRequested();
                if (refreshMetadata || !_metadata.ContainsKey(program.Id))
                {
                    try { _metadata[program.Id] = catalog.Inspect(program); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        _metadata[program.Id] = new("版本未知", "版本未知", null, false, "无法读取安装文件，请检查路径与权限。", WorkerBindingValid: false);
                    }
                }
                var metadata = _metadata[program.Id];
                var match = metadata.WorkerBindingValid ? Match(program, scan)
                    : (State: ProcessState.Unknown, Worker: (WorkerSnapshot?)null, Issue: metadata.Issue);
                var worker = match.Worker;
                if (worker is not null) _emulatorSubmitted.Remove(program.Id);
                double? cpu = null;
                if (worker?.CpuTime is { } total)
                {
                    if (_cpu.TryGetValue(worker.Identity, out var prior))
                        cpu = CalculateCpu(total - prior.Cpu, clock.GetElapsedTime(prior.Time, stamp), Environment.ProcessorCount);
                    nextCpu[worker.Identity] = (stamp, total);
                }

                var issue = match.Issue ?? metadata.Issue;
                var state = match.State == ProcessState.Stopped && !metadata.EntryAvailable ? ProcessState.Unknown : match.State;
                var launchConfirmationTimedOut = false;
                if (_launches.TryGetValue(program.Id, out var launched))
                {
                    if (launched.Emulator && launched.Outcome == CommandStatus.Unknown)
                    {
                        if (worker is null) state = ProcessState.Unknown;
                        issue = UnknownEmulatorLaunch;
                    }
                    else if (worker is not null)
                    {
                        if (launched.Outcome is not null && _launches.Remove(program.Id) && launched.IsRestart)
                            await WriteEvent(program, EventSeverity.Information, "重启后已确认新实例",
                                "已复核新的工作进程身份。", cancellation).ConfigureAwait(false);
                    }
                    else if (launched.Outcome is null || (launched.Outcome == CommandStatus.Accepted &&
                        clock.GetElapsedTime(launched.Timestamp, stamp) < TimeSpan.FromSeconds(25)))
                        state = ProcessState.Starting;
                    else
                    {
                        state = ProcessState.Unknown;
                        launchConfirmationTimedOut = launched.Outcome == CommandStatus.Accepted;
                        issue = launched.Outcome == CommandStatus.Unknown ? UnknownScriptLaunch :
                            "启动后未能确认工作进程。请在外部核对；本次会话暂停再次启动。";
                    }
                }
                _previous.TryGetValue(program.Id, out var previous);
                var expectedExit = previous?.Worker is { } priorWorker && _expectedExits.Contains(priorWorker.Identity);
                if (_busy.TryGetValue(program.Id, out var pending) &&
                    pending is ProgramAction.Close or ProgramAction.Restart or ProgramAction.Force &&
                    !_launches.ContainsKey(program.Id))
                {
                    state = worker is not null ? ProcessState.Stopping : state;
                    issue ??= "操作正在进行。";
                }
                if (pending == ProgramAction.StartWithEmulator && worker is null && state == ProcessState.Stopped)
                {
                    state = ProcessState.Starting;
                    issue = "正在依次启动模拟器和软件。";
                }
                if (worker is null && state == ProcessState.Stopped && !expectedExit)
                {
                    if (previous?.Worker is { } old && _exits.TryGetValue(old.Identity, out var code) && code is > 0)
                        state = ProcessState.ExitedUnexpectedly;
                    else if (previous?.Process == ProcessState.ExitedUnexpectedly)
                        state = ProcessState.ExitedUnexpectedly;
                }

                var upt = worker is null || now < worker.Identity.CreatedAtUtc ? (TimeSpan?)null : now - worker.Identity.CreatedAtUtc;
                var actions = Actions(metadata, worker, state, _busy.ContainsKey(program.Id), _launches.ContainsKey(program.Id));
                if (actions.Contains(ProgramAction.Start) && program is { SupportsEmulatorStartup: true, Emulator.Entry: not null })
                {
                    if (_emulatorSubmitted.Contains(program.Id)) issue ??= EmulatorAlreadySubmitted;
                    else actions.Add(ProgramAction.StartWithEmulator);
                }
                if (worker is { Window: not null }) issue ??= CommandPolicy.GetWindowControlRestriction(worker.WindowControlAllowed);
                if (worker is { Window: { Responding: false } }) issue ??= "窗口无响应。";
                if (worker is { Window: null }) issue ??= "未发现可管理的窗口。";
                if (worker is { CanTerminate: false } && issue is null) issue = "当前权限无法强制结束此实例。";
                PairedStartupProgress? pairedStartup = null;
                if (worker is null && state == ProcessState.Starting)
                {
                    if (_emulatorWaits.TryGetValue(program.Id, out var wait))
                    {
                        var remaining = wait.Duration - clock.GetElapsedTime(wait.StartedAt, stamp);
                        pairedStartup = remaining > TimeSpan.Zero
                            ? new(PairedStartupPhase.Waiting, (int)Math.Ceiling(remaining.TotalSeconds))
                            : new(PairedStartupPhase.StartingScript);
                    }
                    else if (launched?.Paired == true) pairedStartup = new(PairedStartupPhase.StartingScript);
                    else if (pending == ProgramAction.StartWithEmulator) pairedStartup = new(PairedStartupPhase.StartingEmulator);
                }
                var current = new ProgramStatus(program, metadata, state, worker, cpu, worker?.WorkingSet / 1048576d, upt, issue,
                    actions, launchConfirmationTimedOut, EmulatorUsage: emulatorUsage.GetValueOrDefault(program.Id), PairedStartup: pairedStartup);
                await RecordTransition(previous, current, expectedExit, resources, cancellation).ConfigureAwait(false);
                _previous[program.Id] = current;
                statuses.Add(current);
            }
            _cpu.Clear();
            foreach (var (identity, point) in nextCpu) _cpu.Add(identity, point);
            _expectedExits.RemoveWhere(identity => _exits.ContainsKey(identity) ||
                !_programs.Any(program => program.WorkerPaths.Contains(identity.ImagePath, Paths)));
            _exits.Clear();
            var unique = statuses.Where(item => item.Worker is not null).DistinctBy(item => item.Worker!.Identity).ToArray();
            var cpuCoverage = unique.Count(item => item.Cpu is not null);
            var memoryCoverage = unique.Count(item => item.WorkingSetMiB is not null);
            var unknown = statuses.Count(item => item.Worker is null && item.Process is ProcessState.Unknown or ProcessState.Starting);
            return new(now, clock.GetElapsedTime(_started, stamp), statuses,
                resources && unique.Length > 0 && cpuCoverage == unique.Length && unknown == 0 ? unique.Sum(item => item.Cpu) : null,
                resources && unique.Length > 0 && memoryCoverage == unique.Length && unknown == 0 ? unique.Sum(item => item.WorkingSetMiB) : null,
                cpuCoverage, memoryCoverage, unique.Length, unknown,
                _events.Snapshot());
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<(ProgramInstallation Program, CommandResult Result)>> LaunchBatchAsync(
        IReadOnlyList<(ProgramInstallation Program, LaunchEntry Entry)> requests, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _batchActive, 1) != 0)
            throw new InvalidOperationException("批量启动正在进行。");
        try
        {
            var entries = requests.DistinctBy(request => request.Program.Id).ToArray();
            var duplicates = entries.Where(request => request.Program is { SupportsEmulatorStartup: true, Emulator.Entry: not null })
                .GroupBy(request => LauncherKey(request.Program.Emulator!.Entry))
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var results = new List<(ProgramInstallation, CommandResult)>(entries.Length);
            foreach (var (program, entry) in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                CommandResult result;
                if (program.SupportsEmulatorStartup && program.Emulator?.Entry is { } launcher && duplicates.Contains(LauncherKey(launcher)))
                    result = new(CommandStatus.Denied, "多组使用了相同的启动目标和参数，请为每组选择自己的模拟器入口。");
                else
                {
                    var action = program.Kind == ProgramKind.Maa ? ProgramAction.Start : ProgramAction.StartWithEmulator;
                    try { result = await ExecuteAsync(program, action, null, 0, cancellation, entry).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        result = new(CommandStatus.Unknown, "本地操作中断，请检查配置和进程状态。");
                    }
                }
                results.Add((program, result));
            }
            return results;
        }
        finally { Volatile.Write(ref _batchActive, 0); }
    }

    private static (string Target, string Arguments) LauncherKey(LaunchEntry entry) =>
        ((entry.ResolvedTarget ?? entry.TargetPath ?? "").ToUpperInvariant(), entry.Arguments ?? "");

    public async Task<CommandResult> ExecuteAsync(ProgramInstallation requested, ProgramAction action,
        ProcessIdentity? selected, nint window, CancellationToken cancellation, LaunchEntry? confirmedEntry = null)
    {
        var delegated = false;
        var starts = action is ProgramAction.Start or ProgramAction.StartWithEmulator;
        var result = new CommandResult(CommandStatus.Failed, "操作未完成。");
        LaunchEntry? launchEntry = null;
        try
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var program = _programs.Find(item => item.Id == requested.Id);
                if (!ReferenceEquals(program, requested))
                    return new(CommandStatus.IdentityChanged, "安装记录已改变，请重新选择。");
                var needsLaunch = starts || action == ProgramAction.Restart;
                if (needsLaunch && _launches.TryGetValue(requested.Id, out var pending) && pending.Outcome == CommandStatus.Unknown)
                    return new(CommandStatus.Unknown, pending.Emulator ? UnknownEmulatorLaunch : UnknownScriptLaunch);
                if (_busy.ContainsKey(requested.Id) || (needsLaunch && _launches.ContainsKey(requested.Id)))
                    return new(CommandStatus.Denied, "此安装的操作正在处理或启动尚未确认。");
                if (action == ProgramAction.StartWithEmulator && _emulatorSubmitted.Contains(requested.Id))
                    return new(CommandStatus.Denied, EmulatorAlreadySubmitted);
                if (!catalog.HasWorkerBinding(requested))
                    return await ReportResult(requested, action, new(CommandStatus.Denied,
                        "工作程序与此安装不匹配，请重新定位。"), cancellation).ConfigureAwait(false);
                var match = Match(requested, Scan(false, cancellation));
                if (starts)
                {
                    if (match.State != ProcessState.Stopped)
                        return await ReportResult(requested, action, new(CommandStatus.Denied,
                            match.Issue ?? "已有工作进程，请刷新后重试。"), cancellation).ConfigureAwait(false);
                }
                else if (selected is null || match.Worker is not { } worker || !selected.Equals(worker.Identity) ||
                         (action != ProgramAction.Force && (window == 0 || worker.Window?.Handle != window)))
                    return await ReportResult(requested, action, new(CommandStatus.IdentityChanged,
                        match.Issue ?? "原进程或窗口已改变，请重新选择。"), cancellation).ConfigureAwait(false);

                if (needsLaunch)
                {
                    var issue = CheckLaunchEntry(requested, confirmedEntry, out launchEntry);
                    if (issue is not null)
                        return await ReportResult(requested, action, new(CommandStatus.Denied, issue), cancellation).ConfigureAwait(false);
                }
                if (action == ProgramAction.StartWithEmulator)
                {
                    var emulatorIssue = requested.Emulator is { } setup ? catalog.EmulatorIssue(requested, setup)
                        : "请先为此软件设置模拟器。";
                    if (emulatorIssue is not null)
                        return await ReportResult(requested, action, new(CommandStatus.Denied, emulatorIssue), cancellation).ConfigureAwait(false);
                }
                _busy.Add(requested.Id, action);
                delegated = true;
                if (!starts)
                {
                    result = platform.Control(requested.Definition, action == ProgramAction.Restart ? ProgramAction.Close : action,
                        selected!, match.Worker!.Window);
                    if (action != ProgramAction.Activate && result.Status == CommandStatus.Accepted)
                    {
                        _expectedExits.Add(selected!);
                        _awaitingExit[selected!] = false;
                        await WriteEvent(requested, EventSeverity.Information,
                            action == ProgramAction.Force ? "已请求强制结束" : "已请求正常关闭",
                            "等待确认原工作进程退出。", cancellation).ConfigureAwait(false);
                    }
                }
            }
            finally { _gate.Release(); }

            if (action == ProgramAction.Start)
                result = await LaunchAsync(requested, launchEntry!, false, cancellation).ConfigureAwait(false);
            else if (action == ProgramAction.StartWithEmulator)
                result = await LaunchWithEmulatorAsync(requested, launchEntry!, requested.Emulator!, cancellation).ConfigureAwait(false);
            else if (action != ProgramAction.Activate && result.Status == CommandStatus.Accepted)
            {
                result = await WaitForExit(requested, selected!, action == ProgramAction.Restart, cancellation).ConfigureAwait(false);
                if (action == ProgramAction.Restart && result.Status == CommandStatus.Completed)
                    result = await LaunchAsync(requested, launchEntry!, true, cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            if (delegated)
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _busy.Remove(requested.Id);
                    _emulatorWaits.Remove(requested.Id);
                    if (selected is not null && _awaitingExit.Remove(selected, out var exited) && !exited)
                        _expectedExits.Remove(selected);
                }
                finally { _gate.Release(); }
            }
        }
        return await ChangeAsync(() => ReportResult(requested, action, result, cancellation), cancellation).ConfigureAwait(false);
    }

    private string? CheckLaunchEntry(ProgramInstallation program, LaunchEntry? confirmed, out LaunchEntry effective)
    {
        var issue = catalog.Availability(program, out effective);
        if (issue is null && confirmed is not null && effective != confirmed)
            issue = "启动参数或工作目录已变更，请重新确认后启动。";
        if (issue is not null) _metadata.Remove(program.Id);
        return issue;
    }

    private async Task<CommandResult> LaunchWithEmulatorAsync(ProgramInstallation program, LaunchEntry entry, EmulatorStartup setup,
        CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!catalog.HasWorkerBinding(program)) return new(CommandStatus.Denied, "工作程序与此安装不匹配，请重新定位。");
            var match = Match(program, Scan(false, cancellation));
            if (match.State != ProcessState.Stopped)
                return new(CommandStatus.Denied, match.Issue ?? "已有工作进程，启动请求已取消。");
            var issue = CheckLaunchEntry(program, entry, out _) ?? catalog.EmulatorIssue(program, setup);
            if (issue is not null) return new(CommandStatus.Denied, issue);
            _launches[program.Id] = new(clock.GetTimestamp(), null, IsRestart: false, Emulator: true);
        }
        finally { _gate.Release(); }
        var result = await SubmitLaunchAsync(program.Id, setup.Entry, emulator: true, cancellation).ConfigureAwait(false);
        if (result.Status == CommandStatus.Unknown) return result;
        if (result.Status != CommandStatus.Accepted)
            return new(result.Status, $"模拟器启动未提交，软件未启动。{result.Reason}");
        var waitStarted = clock.GetTimestamp();
        var waitDuration = TimeSpan.FromSeconds(setup.WaitSeconds);
        await ChangeAsync(async () =>
        {
            _emulatorWaits[program.Id] = (waitStarted, waitDuration);
            await WriteEvent(program, EventSeverity.Information, "已请求启动模拟器",
                $"等待 {setup.WaitSeconds} 秒后尝试启动软件。", cancellation).ConfigureAwait(false);
            return true;
        }, cancellation).ConfigureAwait(false);
        var remaining = waitDuration - clock.GetElapsedTime(waitStarted);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, clock, cancellation).ConfigureAwait(false);
        result = await LaunchAsync(program, entry, false, cancellation, paired: true).ConfigureAwait(false);
        return result.Status == CommandStatus.Accepted ? result : result with { Reason = $"模拟器启动请求已提交。{result.Reason}" };
    }

    private async Task<CommandResult> LaunchAsync(ProgramInstallation program, LaunchEntry entry, bool restart,
        CancellationToken cancellation, bool paired = false)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!catalog.HasWorkerBinding(program)) return new(CommandStatus.Denied, "工作程序与此安装不匹配，请重新定位。");
            var match = Match(program, Scan(false, cancellation));
            if (match.State != ProcessState.Stopped)
                return new(CommandStatus.Denied, match.Issue ?? "已有工作进程，启动请求已取消。");
            if (CheckLaunchEntry(program, entry, out _) is { } issue) return new(CommandStatus.Denied, issue);
            _launches[program.Id] = new(clock.GetTimestamp(), null, restart, Emulator: false, Paired: paired);
        }
        finally { _gate.Release(); }
        return await SubmitLaunchAsync(program.Id, entry, emulator: false, cancellation).ConfigureAwait(false);
    }

    private async Task<CommandResult> SubmitLaunchAsync(Guid id, LaunchEntry entry, bool emulator, CancellationToken cancellation)
    {
        // ShellExecute can wait for a target-specific UAC decision. Other installations continue to be sampled.
        var invoked = false;
        CommandResult? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                invoked = true;
                try { return platform.Launch(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                    or ArgumentException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    return new CommandResult(CommandStatus.Unknown);
                }
            }, cancellation).ConfigureAwait(false);
            return result.Status == CommandStatus.Unknown
                ? new(CommandStatus.Unknown, emulator ? UnknownEmulatorLaunch : UnknownScriptLaunch) : result;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var pending = _launches[id];
                var outcome = result?.Status ?? (invoked ? CommandStatus.Unknown : (CommandStatus?)null);
                if (pending.Emulator && outcome == CommandStatus.Accepted) _emulatorSubmitted.Add(id);
                if (outcome == CommandStatus.Unknown || (outcome == CommandStatus.Accepted && !pending.Emulator))
                    _launches[id] = pending with { Timestamp = clock.GetTimestamp(), Outcome = outcome };
                else _launches.Remove(id);
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<CommandResult> WaitForExit(ProgramInstallation program, ProcessIdentity identity, bool restart,
        CancellationToken cancellation)
    {
        var start = clock.GetTimestamp();
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellation).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var scan = Scan(false, cancellation);
                var match = Match(program, scan);
                if (_awaitingExit[identity] && (!restart || match.State == ProcessState.Stopped))
                    return new(CommandStatus.Completed, "已确认原进程退出。");
                if (restart && match.Worker is { } replacement && !identity.Equals(replacement.Identity))
                    return new(CommandStatus.Denied, "已有另一工作实例，重启已停止。");
            }
            finally { _gate.Release(); }
        } while (clock.GetElapsedTime(start) < TimeSpan.FromSeconds(8));
        return new(CommandStatus.TimedOut, restart
            ? "等待原进程退出超时，未重新启动。若软件已收起，请从它的托盘菜单退出。"
            : "等待原进程退出超时。若软件已收起，请从它的托盘菜单退出。");
    }

    private ProcessScan Scan(bool resources, CancellationToken cancellation)
    {
        var paths = _programs.SelectMany(program => program.WorkerPaths).ToHashSet(Paths);
        var scan = platform.Scan(paths, resources, cancellation);
        foreach (var (identity, code) in scan.Exits)
        {
            _exits[identity] = code;
            if (_awaitingExit.ContainsKey(identity)) _awaitingExit[identity] = true;
        }
        return scan;
    }

    private static (ProcessState State, WorkerSnapshot? Worker, string? Issue) Match(
        ProgramInstallation program, ProcessScan scan)
    {
        if (!scan.Complete) return (ProcessState.Unknown, null, "未能完成进程枚举。");
        var matches = scan.Workers.Where(worker => program.WorkerPaths.Contains(worker.Identity.ImagePath, Paths)).ToArray();
        if (matches.Length > 1) return (ProcessState.Unknown, null, "检测到多个工作实例，无法选择操作目标。");
        if (program.WorkerPaths.Any(path => scan.UnreadableNames.Contains(Path.GetFileName(path))))
            return (ProcessState.Unknown, null, "存在无法读取身份的候选进程，权限不足或进程已改变。");
        return matches.Length == 1 ? (ProcessState.Running, matches[0], null) : (ProcessState.Stopped, null, null);
    }

    private static HashSet<ProgramAction> Actions(ProgramMetadata metadata, WorkerSnapshot? worker,
        ProcessState state, bool busy, bool launchPending)
    {
        HashSet<ProgramAction> actions = [];
        if (busy) return actions;
        if (!launchPending && metadata.EntryAvailable && state is ProcessState.Stopped or ProcessState.ExitedUnexpectedly)
            actions.Add(ProgramAction.Start);
        if (worker is { CanTerminate: true }) actions.Add(ProgramAction.Force);
        if (worker is { Window: { Responding: true }, WindowControlAllowed: true })
        {
            actions.Add(ProgramAction.Activate);
            actions.Add(ProgramAction.Close);
            if (!launchPending && metadata.EntryAvailable) actions.Add(ProgramAction.Restart);
        }
        return actions;
    }

    public static double? CalculateCpu(TimeSpan difference, TimeSpan interval, int logicalProcessors)
    {
        if (difference < TimeSpan.Zero || interval < TimeSpan.FromMilliseconds(250) ||
            interval > TimeSpan.FromSeconds(15) || logicalProcessors <= 0) return null;
        var rate = 100 * difference.TotalSeconds / interval.TotalSeconds / logicalProcessors;
        return rate <= 105 ? Math.Min(100, rate) : null;
    }

    private async Task RecordTransition(ProgramStatus? previous, ProgramStatus current, bool expectedExit,
        bool resources, CancellationToken cancellation)
    {
        var program = current.Installation;
        if (current.Worker is { } active && (previous?.Worker is not { } old || !active.Identity.Equals(old.Identity)))
            await WriteEvent(program, EventSeverity.Information, "已确认软件运行", "已核对实际映像路径、进程号和创建时间。", cancellation).ConfigureAwait(false);
        if (previous?.Worker is { } prior && current.Worker is null &&
            (current.Process is ProcessState.Stopped or ProcessState.ExitedUnexpectedly || _exits.ContainsKey(prior.Identity)))
        {
            var code = _exits.GetValueOrDefault(prior.Identity);
            var unexpected = !expectedExit && current.Process == ProcessState.ExitedUnexpectedly;
            await WriteEvent(program, unexpected ? EventSeverity.Warning : EventSeverity.Information,
                unexpected ? "疑似异常退出" : "工作进程已退出",
                code is { } known ? $"Windows 退出码 0x{known:X8}。" : "退出原因未知。", cancellation).ConfigureAwait(false);
        }
        if (current.Metadata.Issue != previous?.Metadata.Issue && current.Metadata.Issue is { } pathIssue)
            await WriteEvent(program, EventSeverity.Warning, "安装路径不可用", pathIssue, cancellation).ConfigureAwait(false);
        if (current.Issue != previous?.Issue && current.Issue is { } issue &&
            current.Metadata.Issue != issue && (current.Process == ProcessState.Unknown ||
                current.Worker is { Window: not null, WindowControlAllowed: not true }))
            await WriteEvent(program, EventSeverity.Warning, "观测或权限受限", issue, cancellation).ConfigureAwait(false);
        if (resources && current.Worker is not null && (previous?.Worker is null || previous.WorkingSetMiB != current.WorkingSetMiB))
        {
            if (current.WorkingSetMiB is null)
                await WriteEvent(program, EventSeverity.Warning, "工作集读取失败", "", cancellation).ConfigureAwait(false);
            else if (previous is { Worker: not null, WorkingSetMiB: null })
                await WriteEvent(program, EventSeverity.Information, "工作集读取恢复", "", cancellation).ConfigureAwait(false);
        }
    }

    private async Task<CommandResult> ReportResult(ProgramInstallation program, ProgramAction action, CommandResult result,
        CancellationToken cancellation)
    {
        var severity = result.Status switch
        {
            CommandStatus.Accepted or CommandStatus.Completed => EventSeverity.Information,
            CommandStatus.Failed => EventSeverity.Error,
            _ => EventSeverity.Warning
        };
        var name = action switch
        {
            ProgramAction.Start => "启动软件",
            ProgramAction.StartWithEmulator => "启动模拟器和软件",
            ProgramAction.Close => "正常关闭",
            ProgramAction.Restart => "重启软件",
            ProgramAction.Force => "强制结束",
            _ => "打开窗口"
        };
        await WriteEvent(program, severity, name, result.Reason ?? "", cancellation).ConfigureAwait(false);
        return result;
    }

    private async Task WriteEvent(ProgramInstallation? program, EventSeverity severity, string message, string detail,
        CancellationToken cancellation)
    {
        var entry = new ProgramEvent(clock.GetUtcNow(), program?.Id, program?.Name ?? "BAM", severity, message, detail);
        _events.Add(entry);
        try
        {
            await eventStore.AppendAsync(entry, cancellation).ConfigureAwait(false);
            _eventStoreUnavailable = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AddLogFailure(); }
    }

    private void AddLogFailure()
    {
        if (_eventStoreUnavailable) return;
        _eventStoreUnavailable = true;
        _events.Add(new(clock.GetUtcNow(), null, "BAM", EventSeverity.Warning, "文件日志暂不可用", "本次事件仍保留在运行日志页面。"));
    }

    public void Dispose() => _gate.Dispose();

    private const string UnknownScriptLaunch = "Windows 启动请求的结果未知。BAM 将核对工作进程，确认前暂停再次启动。";
    private const string EmulatorAlreadySubmitted = "此前已提交模拟器启动。核对后可单独启动软件。";
    private const string UnknownEmulatorLaunch = "模拟器启动请求的结果未知，已停止后续启动。请在外部核对，本次会话暂停此安装的启动操作。";
    private sealed record PendingLaunch(long Timestamp, CommandStatus? Outcome, bool IsRestart, bool Emulator, bool Paired = false);
}
