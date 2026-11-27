using BAM.Adapters;
using BAM.Core;
using Xunit;

namespace BAM.Tests;

public sealed class ProgramMonitorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string Root = Path.GetFullPath("fixture");
    private static readonly string WorkerPath = Path.Combine(Root, "MAA.exe");
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly LaunchEntry Entry = new(WorkerPath, WorkerPath, Root, "");
    private static readonly ProgramInstallation Program = new(Id, "MAA", ProgramKind.Maa, Entry, Root, [WorkerPath]);
    private static readonly ProgramWindow Window = new((nint)73, "Fixture window", WindowState.Minimized, true);

    [Theory]
    [InlineData(2, 1, 4, 50)]
    [InlineData(4.01, 1, 4, 100)]
    [InlineData(0, 1, 4, 0)]
    public void CpuNormalizesToSystemCapacity(double busy, double elapsed, int cores, double expected) =>
        Assert.Equal(expected, ProgramMonitor.CalculateCpu(TimeSpan.FromSeconds(busy), TimeSpan.FromSeconds(elapsed), cores));

    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(0.2, 0.01, 4)]
    [InlineData(-0.1, 1, 4)]
    [InlineData(1, 16, 4)]
    [InlineData(8, 1, 4)]
    public void CpuRejectsMissingIntervalCounterResetAndLongSleep(double busy, double elapsed, int cores) =>
        Assert.Null(ProgramMonitor.CalculateCpu(TimeSpan.FromSeconds(busy), TimeSpan.FromSeconds(elapsed), cores));

    [Fact]
    public async Task CpuUsesMonotonicTimeAndRestartsWithNewIdentityOrMissingCounter()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        rig.Platform.Workers = [rig.Worker(17, 1)];
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Cpu);
        rig.Clock.Tick(wallSeconds: -3);
        rig.Platform.Workers = [rig.Worker(17, 1.25)];
        var rollback = await rig.Monitor.SampleAsync(true, Token);
        Assert.Equal(25d / Environment.ProcessorCount, rollback.Cpu);
        Assert.Null(rollback.Programs[0].Uptime);
        rig.Clock.Tick(elapsedSeconds: 70);
        rig.Platform.Workers = [rig.Worker(17, 3)];
        var afterGap = await rig.Monitor.SampleAsync(true, Token);
        Assert.Null(afterGap.Cpu);
        Assert.Equal(2, afterGap.WorkingSetMiB);
        rig.Clock.Tick();
        rig.Platform.Workers = [rig.Worker(17, 3.25)];
        Assert.Equal(25d / Environment.ProcessorCount, (await rig.Monitor.SampleAsync(true, Token)).Cpu);

        rig.Clock.Tick();
        rig.Platform.Workers = [rig.Worker(17, 0.1, creation: 1)];
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Cpu);
        rig.Clock.Tick();
        rig.Platform.Workers = [rig.Worker(17, null, creation: 1)];
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Cpu);
        rig.Clock.Tick();
        rig.Platform.Workers = [rig.Worker(17, 0.8, creation: 1)];
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Cpu);
    }

    [Theory]
    [InlineData(null, "无法读取 BAM 或目标进程令牌的完整性级别，窗口控制暂不可用。")]
    [InlineData(false, "目标窗口的完整性级别高于 BAM，当前权限无法控制。")]
    public async Task WindowRestrictionExplainsTheEvidenceAndPreservesIndependentCapabilities(bool? allowed, string reason)
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        rig.Platform.Workers = [rig.Worker(17, 1) with { WindowControlAllowed = allowed }];
        await rig.Monitor.SampleAsync(true, Token);
        rig.Clock.Tick();
        rig.Platform.Workers = [rig.Worker(17, 1.25) with { WindowControlAllowed = allowed }];
        var frame = await rig.Monitor.SampleAsync(true, Token);
        var app = Assert.Single(frame.Programs);
        Assert.Equal(ProcessState.Running, app.Process);
        Assert.Equal(reason, app.Issue);
        Assert.Equal(25d / Environment.ProcessorCount, app.Cpu);
        Assert.Equal(2, app.WorkingSetMiB);
        Assert.Equal([ProgramAction.Force], app.Actions);
        Assert.Single(frame.EventHistory!.Entries, item => item.Detail == reason);
    }

    [Fact]
    public async Task UnreadableOrMultipleCandidatesBlockStartAndDestructiveActions()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        rig.Platform.Workers = [rig.Worker(17, 1), rig.Worker(18, 1)];
        var multi = Assert.Single((await rig.Monitor.SampleAsync(true, Token)).Programs);
        Assert.Equal(ProcessState.Unknown, multi.Process);
        Assert.Empty(multi.Actions);
        Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
        rig.Platform.Workers = [];
        rig.Platform.Unreadable.Add("MAA.exe");
        Assert.Equal(ProcessState.Unknown, Assert.Single((await rig.Monitor.SampleAsync(true, Token)).Programs).Process);
        Assert.Equal(CommandStatus.IdentityChanged,
            (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Force, rig.Worker(17, 1).Identity, 0, Token)).Status);
        Assert.Empty(rig.Platform.Controls);
        Assert.Equal(0, rig.Platform.Launches);
    }

    [Fact]
    public async Task ReusedPidAndChangedWindowNeverReachNativeControl()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        var original = rig.Worker(17, 1);
        rig.Platform.Workers = [rig.Worker(17, 1, creation: 1)];
        Assert.Equal(CommandStatus.IdentityChanged,
            (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Force, original.Identity, 0, Token)).Status);
        rig.Platform.Workers = [original with { Window = Window with { Handle = 99 } }];
        Assert.Equal(CommandStatus.IdentityChanged,
            (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Close, original.Identity, Window.Handle, Token)).Status);
        Assert.Empty(rig.Platform.Controls);
    }

    [Fact]
    public async Task RestartStopsWhenWindowRefusesCloseAndNeverEscalates()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        var worker = rig.Worker(17, 1);
        rig.Platform.Workers = [worker];
        rig.Platform.ControlResult = new(CommandStatus.Denied, "Fixture denied control.");
        var result = await rig.Monitor.ExecuteAsync(Program, ProgramAction.Restart, worker.Identity, Window.Handle, Token);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal([ProgramAction.Close], rig.Platform.Controls);
        Assert.Equal(0, rig.Platform.Launches);
    }

    [Fact]
    public async Task ConcurrentLaunchAndStaleConfigCannotRepeatTheLaunch()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Platform.Spawn = () =>
        {
            entered.TrySetResult();
            if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var first = rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Monitor.RemoveAsync(Id, Token));
        }
        finally { gate.Set(); }
        Assert.Equal(CommandStatus.Accepted, (await first).Status);
        Assert.Equal(1, rig.Platform.Launches);
        rig.Clock.Tick(elapsedSeconds: 26);
        var status = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Equal(ProcessState.Unknown, status.Process);
        Assert.Empty(status.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationBeforeDispatchAndDefiniteRejectionAllowAnotherAttempt(bool cancelBeforeDispatch)
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var scans = 0;
        rig.Platform.Scanning = () =>
        {
            if (++scans == 2 && cancelBeforeDispatch) cancel.Cancel();
        };
        rig.Platform.LaunchResult = new(CommandStatus.Failed, "Rejected before dispatch");
        var attempt = rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, cancel.Token);
        if (cancelBeforeDispatch)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Empty(rig.Platform.Submitted);
        }
        else Assert.Equal(CommandStatus.Failed, (await attempt).Status);

        Assert.Contains(ProgramAction.Start, Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).Actions);
        rig.Platform.LaunchResult = new(CommandStatus.Accepted);
        Assert.Equal(CommandStatus.Accepted, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
        Assert.Equal(cancelBeforeDispatch ? 1 : 2, rig.Platform.Launches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnknownLaunchBlocksRedispatchUntilExactWorkerIsObserved(bool throwAfterDispatch)
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        rig.Platform.LaunchResult = new(CommandStatus.Unknown);
        if (throwAfterDispatch) rig.Platform.Spawn = () => throw new IOException("fixture lost the result");
        var result = await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token);
        Assert.Equal(CommandStatus.Unknown, result.Status);
        Assert.Contains("结果未知", result.Reason);
        rig.Clock.Tick(elapsedSeconds: 60);
        rig.Platform.Workers = [rig.Worker(17, 1) with
        {
            Identity = new(Path.Combine(Root, "other", "MAA.exe"), 17, DateTimeOffset.UnixEpoch)
        }];
        var unknown = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Equal(ProcessState.Unknown, unknown.Process);
        Assert.Empty(unknown.Actions);
        Assert.Equal(CommandStatus.Unknown, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Monitor.RemoveAsync(Id, Token));

        rig.Platform.Workers = [rig.Worker(42, 1, creation: 2)];
        var confirmed = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Equal(ProcessState.Running, confirmed.Process);
        Assert.Contains(ProgramAction.Activate, confirmed.Actions);
        Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
        Assert.Single(rig.Platform.Submitted);
    }

    [Fact]
    public async Task UnknownEmulatorLaunchPreventsRetryAndNeverDispatchesTheScript()
    {
        using var rig = new Rig();
        var program = rig.SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 5");
        await rig.Monitor.InitializeAsync(Token);
        rig.Platform.Spawn = () => throw new TimeoutException();
        var result = await rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token);
        Assert.Equal(CommandStatus.Unknown, result.Status);
        Assert.Contains("模拟器启动请求的结果未知", result.Reason);
        Assert.Equal(ProcessState.Unknown, Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).Process);
        Assert.Equal(CommandStatus.Unknown, (await rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token)).Status);
        Assert.Equal(program.Emulator!.Entry, Assert.Single(rig.Platform.Submitted));
    }

    [Fact]
    public async Task CancellationAfterPlatformEntryPreservesTheUncertainLaunch()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        rig.Platform.Spawn = () =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, cancel.Token));
        Assert.Equal(CommandStatus.Unknown, (await rig.Monitor.ExecuteAsync(Program, ProgramAction.Start, null, 0, Token)).Status);
        Assert.Single(rig.Platform.Submitted);
    }

    [Fact]
    public async Task AnUnrequestedNonzeroExitIsLoggedOnce()
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        var worker = rig.Worker(17, 1);
        rig.Platform.Workers = [worker];
        await rig.Monitor.SampleAsync(true, Token);
        rig.Platform.Workers = [];
        rig.Platform.Exits.Add(worker.Identity, 23);
        Assert.Equal(ProcessState.ExitedUnexpectedly, (await rig.Monitor.SampleAsync(true, Token)).Programs[0].Process);
        var following = await rig.Monitor.SampleAsync(true, Token);
        Assert.Equal(ProcessState.ExitedUnexpectedly, following.Programs[0].Process);
        Assert.Single(following.EventHistory!.Entries, item => item.Message == "疑似异常退出");
    }

    [Fact]
    public async Task EventHistoryCountsReplayAndLiveEvictionsInTheSameFrame()
    {
        using var rig = new Rig();
        var logs = Enumerable.Range(0, ProgramEventHistory.Capacity).Reverse().Select(index =>
            new ProgramEvent(DateTimeOffset.UnixEpoch, null, "BAM", EventSeverity.Information, $"历史记录 {index}", "")).ToArray();
        rig.Store.History = new(logs, 100);
        await rig.Monitor.InitializeAsync(Token);
        var initial = (await rig.Monitor.SampleAsync(false, Token)).EventHistory!;
        Assert.Equal(101, initial.OlderEntriesExcluded);
        Assert.Equal(ProgramEventHistory.Capacity, initial.Entries.Count);
        Assert.Equal("BAM 已启动", initial.Entries[0].Message);
        Assert.Same(initial, (await rig.Monitor.SampleAsync(false, Token)).EventHistory);

        rig.Platform.Workers = [rig.Worker(17, 1)];
        var updated = (await rig.Monitor.SampleAsync(false, Token)).EventHistory!;
        Assert.Equal(102, updated.OlderEntriesExcluded);
        Assert.Equal("已确认软件运行", updated.Entries[0].Message);
        Assert.Equal(101, initial.OlderEntriesExcluded);
        Assert.Equal("BAM 已启动", initial.Entries[0].Message);
    }

    [Fact]
    public async Task DiscoveryKeepsManualConfigurationAndDeduplicatesWorkerPaths()
    {
        using var rig = new Rig();
        var first = Program with { Name = "手动设置" };
        rig.Store.Programs = [first, Program with { Id = Guid.NewGuid() }];
        rig.Files.Links[Path.Combine(Root, "MAA.lnk")] = Entry with { OriginalPath = Path.Combine(Root, "MAA.lnk") };
        await rig.Monitor.InitializeAsync(Token);
        Assert.Equal(0, await rig.Monitor.DiscoverAsync(Token));
        Assert.Equal("手动设置", Assert.Single((await rig.Monitor.SampleAsync(true, Token)).Programs).Installation.Name);
        Assert.Equal(0, rig.Store.Saves);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("entry")]
    [InlineData("resolved")]
    public async Task RestoredKnownInstallRejectsMismatchedWorkerOrDirectEntry(string mismatch)
    {
        using var rig = new Rig();
        var other = Path.Combine(Root, "other", "MAA.exe");
        rig.Store.Programs = [mismatch switch
        {
            "entry" => Program with { Entry = Entry with { OriginalPath = other } },
            "resolved" => Program with { Entry = Entry with { ResolvedTarget = other }, WorkerPaths = [other] },
            _ => Program with { WorkerPaths = [other] }
        }];
        await rig.Monitor.InitializeAsync(Token);
        var frame = await rig.Monitor.SampleAsync(false, Token);
        if (mismatch == "entry") Assert.Empty(frame.Programs);
        else
        {
            var row = Assert.Single(frame.Programs);
            Assert.Equal(ProcessState.Unknown, row.Process);
            Assert.Empty(row.Actions);
            Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(row.Installation, ProgramAction.Start, null, 0, Token)).Status);
            Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(row.Installation, ProgramAction.Force,
                new(other, 17, DateTimeOffset.UnixEpoch), 0, Token)).Status);
        }
        Assert.Empty(rig.Platform.Controls);
        Assert.Equal(0, rig.Platform.Launches);
    }

    [Fact]
    public async Task SaveBeforeFirstSamplePreservesExistingInstalls()
    {
        using var rig = new Rig();
        var other = Path.Combine(Root, "other", "MAA.exe");
        var second = Program with { Id = Guid.NewGuid(), Entry = Entry with { OriginalPath = other, TargetPath = other }, WorkerPaths = [other] };
        await rig.Monitor.SaveAsync(second, Token);
        Assert.Equal(new[] { Program, second }, rig.Store.Programs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRequiresOriginalHandleExitEvenWhenSamplingRunsFirst(bool signaled)
    {
        using var rig = new Rig();
        await rig.Monitor.InitializeAsync(Token);
        var worker = rig.Worker(17, 1);
        rig.Platform.Workers = [worker];
        var restart = rig.Monitor.ExecuteAsync(Program, ProgramAction.Restart, worker.Identity, Window.Handle, Token);
        Assert.Single(rig.Platform.Controls);
        rig.Platform.Workers = [];
        if (signaled) rig.Platform.Exits.Add(worker.Identity, null);
        await rig.Monitor.SampleAsync(false, Token);
        rig.Clock.Tick(elapsedSeconds: 9);
        Assert.Equal(signaled ? CommandStatus.Accepted : CommandStatus.TimedOut, (await restart).Status);
        Assert.Equal(signaled ? 1 : 0, rig.Platform.Launches);
    }

    [Fact]
    public async Task DiscoveryDoesNotReaddRemovedRecordAndManualImportCanRestoreIt()
    {
        using var rig = new Rig();
        var link = Path.Combine(Root, "MAA.lnk");
        rig.Files.Links[link] = Entry with { OriginalPath = link };
        await rig.Monitor.InitializeAsync(Token);
        await rig.Monitor.RemoveAsync(Id, Token);
        Assert.Empty(rig.Store.Programs);
        Assert.Contains(WorkerPath, rig.Store.Suppressed);
        Assert.Equal(0, await rig.Monitor.DiscoverAsync(Token));
        Assert.Empty(rig.Platform.Controls);
        var imported = new InstallationCatalog(rig.Files).Import(link);
        await rig.Monitor.SaveAsync(imported, Token);
        Assert.Single(rig.Store.Programs);
        Assert.DoesNotContain(WorkerPath, rig.Store.Suppressed);
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, true)]
    public async Task ShortcutFieldsAreInheritedIndependentlyAndConfirmedBeforeDispatch(bool inheritArgs,
        bool inheritDirectory, bool changeArgs, bool changeDirectory)
    {
        using var rig = new Rig();
        var link = Path.Combine(Root, "MAA.lnk");
        var initial = rig.Files.Links[link] = Entry with { OriginalPath = link, Arguments = "--original" };
        var catalog = new InstallationCatalog(rig.Files);
        var imported = catalog.Import(link);
        Assert.True(imported.InheritShortcutArguments);
        Assert.True(imported.InheritShortcutDirectory);
        var program = catalog.Edit(imported with
        {
            InheritShortcutArguments = inheritArgs,
            InheritShortcutDirectory = inheritDirectory
        }, imported.Name, inheritDirectory ? Root : Path.Combine(Root, "custom"), inheritArgs ? initial.Arguments : "--custom", null);
        rig.Store.Programs = [program];
        await rig.Monitor.InitializeAsync(Token);
        var prior = Assert.IsType<LaunchEntry>(Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).Metadata.EffectiveEntry);
        rig.Files.Links[link] = initial with
        {
            Arguments = changeArgs ? "--from-link" : initial.Arguments,
            WorkingDirectory = changeDirectory ? Path.Combine(Root, "link-directory") : Root
        };

        if ((inheritArgs && changeArgs) || (inheritDirectory && changeDirectory))
        {
            Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token, prior)).Status);
            Assert.Empty(rig.Platform.Submitted);
        }
        var current = Assert.IsType<LaunchEntry>(Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).Metadata.EffectiveEntry);
        Assert.Equal(inheritArgs ? rig.Files.Links[link].Arguments : "--custom", current.Arguments);
        Assert.Equal(inheritDirectory ? rig.Files.Links[link].WorkingDirectory : Path.Combine(Root, "custom"), current.WorkingDirectory);
        Assert.Equal(CommandStatus.Accepted, (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token, current)).Status);
        Assert.Equal(current, Assert.Single(rig.Platform.Submitted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShortcutTargetAndElevationChangesAreRejectedDespiteFieldInheritance(bool targetChanged)
    {
        using var rig = new Rig();
        var link = Path.Combine(Root, "MAA.lnk");
        var initial = rig.Files.Links[link] = Entry with { OriginalPath = link };
        var program = new InstallationCatalog(rig.Files).Import(link);
        rig.Store.Programs = [program];
        await rig.Monitor.InitializeAsync(Token);
        rig.Files.Links[link] = targetChanged ? initial with { TargetPath = Path.Combine(Root, "another.exe") } : initial with { RunAs = true };
        Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token, program.Entry)).Status);
        Assert.Empty(rig.Platform.Submitted);
    }

    [Fact]
    public async Task ResourceSummaryHidesPartialDataWhenAnotherInstallCannotBeVerified()
    {
        using var rig = new Rig();
        rig.Store.Programs = [Program, new(Guid.NewGuid(), "Generic", ProgramKind.Generic,
            Entry with { OriginalPath = Path.Combine(Root, "second.exe"), TargetPath = Path.Combine(Root, "second.exe") },
            Root, [Path.Combine(Root, "second.exe")])];
        rig.Platform.Workers = [rig.Worker(17, 1)];
        rig.Platform.Unreadable.Add("second.exe");
        await rig.Monitor.InitializeAsync(Token);
        var frame = await rig.Monitor.SampleAsync(true, Token);
        Assert.Equal(1, frame.ConfirmedWorkers);
        Assert.Equal(1, frame.MemoryCoverage);
        Assert.Equal(1, frame.UnknownPrograms);
        Assert.Null(frame.WorkingSetMiB);
    }

    [Theory]
    [InlineData("\"two words\"", true)]
    [InlineData("\"unbalanced", false)]
    [InlineData("%PATH%", false)]
    [InlineData("\"text & elsewhere\"", false)]
    [InlineData("literal\nother", false)]
    public void BatchLaunchFailsClosedForDynamicShellSyntax(string arguments, bool expected)
    {
        var entry = Entry with { TargetPath = Path.Combine(Root, "启动.cmd"), Arguments = arguments };
        Assert.Equal(expected, entry.HasSafeBatchArguments);
    }

    [Theory]
    [InlineData(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 1")]
    [InlineData(ProgramKind.BBchannel, "启动.cmd", "--instance 2")]
    public async Task EmulatorShortcutRunsBeforeTheOriginalScriptEntry(ProgramKind kind, string filename, string arguments)
    {
        using var rig = new Rig();
        var program = rig.SetEmulatorStartup(kind, filename, arguments);
        await rig.Monitor.InitializeAsync(Token);
        var operation = rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token);
        await rig.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(program.Emulator!.Entry, Assert.Single(rig.Platform.Submitted));
        Assert.Equal(arguments, rig.Platform.Submitted[0].Arguments);
        var waiting = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Equal(ProcessState.Starting, waiting.Process);
        Assert.Equal(new(PairedStartupPhase.Waiting, 30), waiting.PairedStartup);
        Assert.Empty(waiting.Actions);
        Assert.Equal(CommandStatus.Denied,
            (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token)).Status);
        rig.Clock.Tick(wallSeconds: -120, elapsedSeconds: 29);
        var almostReady = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Equal(new(PairedStartupPhase.Waiting, 1), almostReady.PairedStartup);
        Assert.Single(rig.Platform.Submitted);
        rig.Clock.Tick();
        Assert.Equal(CommandStatus.Accepted, (await operation).Status);
        Assert.Equal(new[] { program.Emulator.Entry, program.Entry }, rig.Platform.Submitted);
        Assert.Empty(rig.Platform.Controls);
        Assert.Equal(new(PairedStartupPhase.StartingScript), Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).PairedStartup);
        rig.Platform.Workers = [MetricWorker(program.WorkerPaths[0], 42, 1)];
        Assert.Null(Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).PairedStartup);
    }

    [Fact]
    public async Task PendingEmulatorRequestHasNoCountdownBeforeAcceptance()
    {
        using var rig = new Rig();
        var program = rig.SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 1");
        await rig.Monitor.InitializeAsync(Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Platform.LaunchResult = new(CommandStatus.Denied);
        rig.Platform.Spawn = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        var operation = rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            rig.Clock.Tick(elapsedSeconds: 15);
            Assert.Equal(new(PairedStartupPhase.StartingEmulator),
                Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).PairedStartup);
        }
        finally { release.Set(); }
        Assert.Equal(CommandStatus.Denied, (await operation).Status);
        Assert.Null(Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).PairedStartup);
        Assert.Single(rig.Platform.Submitted);
    }

    [Theory]
    [InlineData("changed shortcut")]
    [InlineData("launch denied")]
    [InlineData("script already running")]
    [InlineData("cancelled")]
    public async Task EmulatorStartupAbortsBeforeLaunchingTheScriptWhenBlocked(string blocker)
    {
        using var rig = new Rig();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var program = rig.SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 3");
        var entry = program.Emulator!.Entry;
        if (blocker == "changed shortcut") rig.Files.Links[entry.OriginalPath] = entry with { Arguments = "--instance 4" };
        if (blocker == "launch denied") rig.Platform.LaunchResult = new(CommandStatus.Denied);
        await rig.Monitor.InitializeAsync(Token);
        var operation = rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, stop.Token);
        if (blocker is "script already running" or "cancelled")
        {
            await rig.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (blocker == "script already running")
            {
                rig.Platform.Workers = [rig.Worker(17, 1) with
                {
                    Identity = new(program.WorkerPaths[0], 17, DateTimeOffset.UnixEpoch)
                }];
                rig.Clock.Tick(elapsedSeconds: 30);
            }
            else stop.Cancel();
        }

        if (blocker == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else Assert.Equal(CommandStatus.Denied, (await operation).Status);
        if (blocker == "changed shortcut") Assert.Empty(rig.Platform.Submitted);
        else Assert.Equal(entry, Assert.Single(rig.Platform.Submitted));
        Assert.Empty(rig.Platform.Controls);
        Assert.Null(Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs).PairedStartup);
    }

    [Fact]
    public async Task OrdinaryStartDoesNotUseTheEmulatorConfiguration()
    {
        using var rig = new Rig();
        var program = rig.SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 1");
        await rig.Monitor.InitializeAsync(Token);
        Assert.Equal(CommandStatus.Accepted, (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token)).Status);
        Assert.Equal(program.Entry, Assert.Single(rig.Platform.Submitted));
    }

    [Fact]
    public async Task BatchUsesMaaOwnStartupAndPreservesOrderAndConcurrencyChecks()
    {
        using var rig = new Rig();
        var programs = rig.SetStartupBatch();
        await rig.Monitor.InitializeAsync(Token);
        var requested = programs.Select(program => (program, program.Entry)).ToArray();
        Assert.Equal(CommandStatus.Denied, (await rig.Monitor.ExecuteAsync(programs[1], ProgramAction.StartWithEmulator,
            null, 0, Token, programs[1].Entry)).Status);
        Assert.Empty(rig.Platform.Submitted);
        var actions = (await rig.Monitor.SampleAsync(false, Token)).Programs.Single(item => item.Installation == programs[1]).Actions;
        Assert.Contains(ProgramAction.Start, actions);
        Assert.DoesNotContain(ProgramAction.StartWithEmulator, actions);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        rig.Platform.Spawn = () =>
        {
            if (rig.Platform.Launches != 1) return;
            started.SetResult();
            if (!proceed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        var batch = rig.Monitor.LaunchBatchAsync([.. requested, requested[0]], Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Monitor.LaunchBatchAsync(requested, Token));
        }
        finally { proceed.Set(); }
        var results = await batch;
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(CommandStatus.Accepted, result.Result.Status));
        Assert.Equal([programs[0].Emulator!.Entry, programs[0].Entry, programs[1].Entry,
            programs[2].Emulator!.Entry, programs[2].Entry], rig.Platform.Submitted);
        Assert.Empty(rig.Platform.Controls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchSkipsRunningOrConflictingPairsAndStillStartsMaa(bool duplicate)
    {
        using var rig = new Rig();
        var programs = rig.SetStartupBatch(duplicate);
        await rig.Monitor.InitializeAsync(Token);
        if (!duplicate)
        {
            rig.Platform.Workers = [MetricWorker(programs[0].WorkerPaths[0], 111, 12)];
            var last = programs[2].Emulator!.Entry;
            rig.Files.Links[last.OriginalPath] = last with { Arguments = "--unexpected" };
        }
        var results = await rig.Monitor.LaunchBatchAsync(programs.Select(program => (program, program.Entry)).ToArray(), Token);
        Assert.Equal(new[] { CommandStatus.Denied, CommandStatus.Accepted, CommandStatus.Denied },
            results.Select(item => item.Result.Status));
        Assert.Equal(programs[1].Entry, Assert.Single(rig.Platform.Submitted));
        Assert.Empty(rig.Platform.Controls);
    }

    [Fact]
    public async Task AnEditedShortcutCannotDispatchItsScriptAfterTheEmulatorWait()
    {
        using var rig = new Rig();
        var configured = rig.SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 3");
        var link = Path.ChangeExtension(configured.Entry.OriginalPath, ".lnk");
        var original = rig.Files.Links[link] = configured.Entry with { OriginalPath = link };
        var program = new InstallationCatalog(rig.Files).Import(link) with { Emulator = configured.Emulator };
        rig.Store.Programs = [program];
        await rig.Monitor.InitializeAsync(Token);
        var action = rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token, original);
        await rig.Clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        rig.Files.Links[link] = original with { Arguments = "--changed" };
        rig.Clock.Tick(elapsedSeconds: 30);
        Assert.Equal(CommandStatus.Denied, (await action).Status);
        Assert.Equal(program.Emulator!.Entry, Assert.Single(rig.Platform.Submitted));
        var next = Assert.Single((await rig.Monitor.SampleAsync(false, Token)).Programs);
        Assert.Contains(ProgramAction.Start, next.Actions);
        Assert.DoesNotContain(ProgramAction.StartWithEmulator, next.Actions);
        var retry = await rig.Monitor.ExecuteAsync(program, ProgramAction.StartWithEmulator, null, 0, Token, next.Metadata.EffectiveEntry);
        Assert.Contains("此前已提交模拟器启动", retry.Reason);
        Assert.Single(rig.Platform.Submitted);
        Assert.Equal(CommandStatus.Accepted, (await rig.Monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token,
            next.Metadata.EffectiveEntry)).Status);
        Assert.Equal(2, rig.Platform.Submitted.Count);
    }

    [Fact]
    public async Task EmulatorProcessesByDistinctImageContributeOnlyToTheirOwnCard()
    {
        using var rig = new Rig();
        var front = Path.Combine(Root, "front.exe");
        var vm = Path.Combine(Root, "vm.exe");
        var other = Path.Combine(Root, "other", "front.exe");
        var secondEntry = Path.Combine(Root, "another.exe");
        rig.Store.Programs = [Program with { EmulatorMonitor = new(front, WorkerImagePath: vm) },
            new(Guid.NewGuid(), "Another", ProgramKind.Generic, new(secondEntry, secondEntry, Root, ""), Root, [secondEntry])
            { EmulatorMonitor = new(other) }];
        rig.Platform.EmulatorWorkers = [MetricWorker(front, 40, 128), MetricWorker(vm, 41, 512), MetricWorker(other, 42, 256)];
        await rig.Monitor.InitializeAsync(Token);

        var first = await rig.Monitor.SampleAsync(true, Token);
        Assert.Equal(640, first.Programs[0].EmulatorUsage!.WorkingSetMiB);
        Assert.Equal(256, first.Programs[1].EmulatorUsage!.WorkingSetMiB);
        Assert.Null(first.Programs[0].EmulatorUsage!.Cpu);
        Assert.Null(first.WorkingSetMiB);
        rig.Clock.Tick();
        rig.Platform.EmulatorWorkers = rig.Platform.EmulatorWorkers.Select(worker => worker with { CpuTime = TimeSpan.FromSeconds(.1) }).ToArray();
        var second = await rig.Monitor.SampleAsync(true, Token);
        Assert.Equal(20d / Environment.ProcessorCount, second.Programs[0].EmulatorUsage!.Cpu);
        Assert.Equal(10d / Environment.ProcessorCount, second.Programs[1].EmulatorUsage!.Cpu);

        rig.Platform.EmulatorWorkers = rig.Platform.EmulatorWorkers.Where(worker => worker.Identity.ImagePath != vm).ToArray();
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Programs[0].EmulatorUsage!.WorkingSetMiB);
        var scanCount = rig.Platform.EmulatorScans;
        Assert.Null((await rig.Monitor.SampleAsync(false, Token)).Programs[1].EmulatorUsage!.WorkingSetMiB);
        Assert.Equal(scanCount, rig.Platform.EmulatorScans);
        Assert.Empty(rig.Platform.Controls);
        Assert.Equal(0, rig.Platform.Launches);
    }

    [Theory]
    [InlineData(1, "--index=1", 376)]
    [InlineData(0, "--index=1", null)]
    [InlineData(1, "--index=0", null)]
    [InlineData(1, "--index=unknown", null)]
    [InlineData(null, "--index=1", null)]
    public async Task SharedEmulatorExeRequiresUniqueConsistentInstanceNumbers(int? secondIndex, string secondFlag, int? expectedSecond)
    {
        using var rig = new Rig();
        var image = Path.Combine(Root, "emulator.exe");
        var secondEntry = Path.Combine(Root, "another.exe");
        rig.Store.Programs = [Program with { EmulatorMonitor = new(image, 0) },
            new(Guid.NewGuid(), "Another", ProgramKind.Generic, new(secondEntry, secondEntry, Root, ""), Root, [secondEntry])
            { EmulatorMonitor = new(image, secondIndex) }];
        var firstWorker = MetricWorker(image, 40, 192);
        var secondWorker = MetricWorker(image, 41, 376);
        rig.Platform.EmulatorWorkers = [firstWorker, secondWorker];
        rig.Platform.EmulatorArguments[firstWorker.Identity] = [image, "-v", "0"];
        rig.Platform.EmulatorArguments[secondWorker.Identity] = [image, secondFlag];
        await rig.Monitor.InitializeAsync(Token);

        var initial = await rig.Monitor.SampleAsync(true, Token);
        Assert.Null(initial.Programs[0].EmulatorUsage!.WorkingSetMiB);
        Assert.Null(initial.Programs[1].EmulatorUsage!.WorkingSetMiB);
        await rig.Platform.ReadArguments.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        var firstExpected = secondIndex != 0 && secondFlag == "--index=1" ? 192 : (int?)null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        ProgramFrame frame;
        do
        {
            frame = await rig.Monitor.SampleAsync(true, deadline.Token);
            if (frame.Programs[0].EmulatorUsage!.Hint != "正在核对模拟器实例编号。" &&
                frame.Programs[0].EmulatorUsage!.WorkingSetMiB == firstExpected &&
                frame.Programs[1].EmulatorUsage!.WorkingSetMiB == expectedSecond) break;
            await Task.Delay(10, deadline.Token);
        } while (true);
        if (expectedSecond is not null)
        {
            rig.Platform.EmulatorWorkers = [firstWorker, secondWorker with { Identity = new(image, 41, DateTimeOffset.UnixEpoch.AddSeconds(1)) }];
            Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Programs[1].EmulatorUsage!.WorkingSetMiB);
        }
    }

    [Fact]
    public async Task ThreeCardsReportIndependentEmulatorMetricsIncludingMaa()
    {
        using var rig = new Rig();
        var programs = rig.SetStartupBatch();
        var sharedImage = Path.Combine(Root, "shared-vm.exe");
        var separateImage = Path.Combine(Root, "independent-vm.exe");
        rig.Store.Programs = [programs[0] with { EmulatorMonitor = new(sharedImage, 0) },
            programs[1] with { Emulator = null, EmulatorMonitor = new(separateImage) },
            programs[2] with { EmulatorMonitor = new(sharedImage, 1) }];
        var first = MetricWorker(sharedImage, 40, 192);
        var second = MetricWorker(separateImage, 41, 256);
        var third = MetricWorker(sharedImage, 42, 384);
        rig.Platform.EmulatorWorkers = [first, second, third];
        rig.Platform.EmulatorArguments[first.Identity] = [sharedImage, "-v", "0"];
        rig.Platform.EmulatorArguments[third.Identity] = [sharedImage, "--index=1"];
        await rig.Monitor.InitializeAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while ((await rig.Monitor.SampleAsync(true, deadline.Token)).Programs[2].EmulatorUsage!.WorkingSetMiB is null)
            await Task.Delay(10, deadline.Token);

        rig.Clock.Tick();
        rig.Platform.EmulatorWorkers = [first with { CpuTime = TimeSpan.FromSeconds(.1) },
            second with { CpuTime = TimeSpan.FromSeconds(.2) }, third with { CpuTime = TimeSpan.FromSeconds(.3) }];
        var sample = await rig.Monitor.SampleAsync(true, Token);
        var measured = sample.Programs.Select(item => item.EmulatorUsage!).ToArray();
        Assert.Collection(measured,
            usage => Verify(usage, 192, 10), usage => Verify(usage, 256, 20), usage => Verify(usage, 384, 30));
        Assert.All(measured, usage => Assert.Equal(sample.ObservedAt, usage.SampledAt));

        rig.Platform.EmulatorWorkers = [first, second];
        var afterExit = (await rig.Monitor.SampleAsync(true, Token)).Programs.Select(item => item.EmulatorUsage!).ToArray();
        Assert.Equal(192, afterExit[0].WorkingSetMiB);
        Assert.Equal(256, afterExit[1].WorkingSetMiB);
        Assert.Null(afterExit[2].WorkingSetMiB);
        Assert.Null(afterExit[2].SampledAt);
        Assert.Contains("没有与配置编号匹配", afterExit[2].Hint);
        var paused = await rig.Monitor.SampleAsync(false, Token);
        Assert.All(paused.Programs, item => Assert.Null(item.EmulatorUsage!.SampledAt));
        Assert.Empty(rig.Platform.Submitted);
        Assert.Empty(rig.Platform.Controls);

        static void Verify(EmulatorResourceUsage usage, double memory, double cpu)
        {
            Assert.Equal(memory, usage.WorkingSetMiB);
            Assert.Equal(cpu / Environment.ProcessorCount, usage.Cpu);
        }
    }

    private static WorkerSnapshot MetricWorker(string path, int pid, long memory) =>
        new(new(path, pid, DateTimeOffset.UnixEpoch), TimeSpan.Zero, memory * 1048576, null, null, false);

    private sealed class Rig : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public FakePlatform Platform { get; } = new();
        public FakeStore Store { get; } = new();
        public FakeFiles Files { get; } = new();
        public ProgramMonitor Monitor { get; }

        public Rig() => Monitor = new(new(Files), Platform, Store, Store, Clock);

        public ProgramInstallation SetEmulatorStartup(ProgramKind kind, string filename, string arguments)
        {
            var target = Path.Combine(Root, "emulator.exe");
            var shortcut = Path.Combine(Root, $"{kind}-instance.lnk");
            var emulator = Files.Links[shortcut] = new(shortcut, target, Root, arguments);
            var entry = Path.Combine(Root, filename);
            var worker = kind == ProgramKind.BBchannel ? Path.Combine(Root, @"dist\BBchannel64\BBchannel.exe") : entry;
            var program = new ProgramInstallation(Id, kind.ToString(), kind, new(entry, entry, Root, ""), Root, [worker])
            {
                Emulator = new(emulator, 30)
            };
            Store.Programs = [program];
            return program;
        }

        public ProgramInstallation[] SetStartupBatch(bool duplicate = false)
        {
            var programs = new[]
            {
                SetEmulatorStartup(ProgramKind.MaaEnd, "MaaEnd.exe", "--instance 1"),
                SetEmulatorStartup(ProgramKind.Maa, "MAA.exe", "--instance 1"),
                SetEmulatorStartup(ProgramKind.BBchannel, "启动.cmd", duplicate ? "--instance 1" : "--instance 3")
            }.Select(program => program with { Id = Guid.NewGuid(), Emulator = program.Emulator! with { WaitSeconds = 0 } }).ToArray();
            Store.Programs = programs;
            return programs;
        }

        public WorkerSnapshot Worker(int pid, double? cpu, int creation = 0) => new(
            new(WorkerPath, pid, DateTimeOffset.UnixEpoch.AddSeconds(creation)),
            cpu is { } seconds ? TimeSpan.FromSeconds(seconds) : null, 2 * 1048576L, Window, true, true);

        public void Dispose() => Monitor.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _wall = DateTimeOffset.UnixEpoch;
        private Timer? _delay;
        private long _due;
        public TaskCompletionSource DelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _wall;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Assert.Null(_delay);
            _due = _ticks + dueTime.Ticks;
            var timer = _delay = new Timer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            DelayScheduled.TrySetResult();
            return timer;
        }

        public void Tick(int wallSeconds = 1, int elapsedSeconds = 1)
        {
            _ticks += elapsedSeconds * TimeSpan.TicksPerSecond;
            _wall = _wall.AddSeconds(wallSeconds);
            if (_delay is { } timer && _ticks >= _due)
            {
                _delay = null;
                timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private sealed class FakePlatform : IProgramPlatform
    {
        public IReadOnlyList<WorkerSnapshot> Workers = [];
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ProcessIdentity, uint?> Exits { get; } = [];
        public List<ProgramAction> Controls { get; } = [];
        public List<LaunchEntry> Submitted { get; } = [];
        public IReadOnlyList<WorkerSnapshot> EmulatorWorkers = [];
        public Dictionary<ProcessIdentity, string[]> EmulatorArguments { get; } = [];
        public TaskCompletionSource ReadArguments { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _argumentsRead;
        public int EmulatorScans;
        public int Launches;
        public Action? Spawn;
        public Action? Scanning;
        public CommandResult ControlResult = new(CommandStatus.Accepted);
        public CommandResult LaunchResult = new(CommandStatus.Accepted);

        public ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, CancellationToken cancellation)
        {
            Scanning?.Invoke();
            var copy = new Dictionary<ProcessIdentity, uint?>(Exits);
            Exits.Clear();
            return new(true, Unreadable, Workers, copy);
        }

        public CommandResult Launch(LaunchEntry entry)
        {
            Launches++;
            Submitted.Add(entry);
            Spawn?.Invoke();
            return LaunchResult;
        }

        public ProcessScan ScanEmulators(IReadOnlySet<string> paths, CancellationToken cancellation)
        {
            EmulatorScans++;
            return new(true, Unreadable, EmulatorWorkers.Where(worker => paths.Contains(worker.Identity.ImagePath)).ToArray(),
                new Dictionary<ProcessIdentity, uint?>());
        }

        public string[]? ReadEmulatorArguments(ProcessIdentity identity, CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref _argumentsRead) == 2) ReadArguments.TrySetResult();
            return EmulatorArguments.GetValueOrDefault(identity);
        }

        public CommandResult Control(ScriptDefinition script, ProgramAction action, ProcessIdentity identity, ProgramWindow? window)
        {
            Controls.Add(action);
            return ControlResult;
        }
    }

    private sealed class FakeFiles : IProgramFiles
    {
        public Dictionary<string, LaunchEntry> Links { get; } = [];
        public IEnumerable<string> FindShortcuts(CancellationToken cancellation) => Links.Keys;
        public LaunchEntry ReadEntry(string path) => Links[path];
        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public string? ReadSmallText(string path) => null;
        public string? ReadProductVersion(string path) => null;
        public byte[]? ReadIcon(string path, int index) => null;
    }

    private sealed class FakeStore : IProgramStore, IProgramEventStore
    {
        public ProgramEventHistory History = new([]);
        public IReadOnlyList<ProgramInstallation> Programs = [Program];
        public int Saves;
        public IReadOnlyList<string> Suppressed { get; private set; } = [];
        public Task<ProgramStoreLoad> LoadAsync(CancellationToken cancellation) =>
            Task.FromResult(new ProgramStoreLoad(Programs, Suppressed, null));
        public Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths, CancellationToken cancellation)
        {
            Programs = programs;
            Suppressed = suppressedPaths;
            Saves++;
            return Task.CompletedTask;
        }
        Task<ProgramEventHistory> IProgramEventStore.LoadAsync(CancellationToken cancellation) =>
            Task.FromResult(History);
        public Task AppendAsync(ProgramEvent entry, CancellationToken cancellation) => Task.CompletedTask;
    }
}
