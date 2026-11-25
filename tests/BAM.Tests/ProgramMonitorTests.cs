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
        Assert.Null((await rig.Monitor.SampleAsync(true, Token)).Cpu);

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
        Assert.Equal(ProcessState.ExitedUnexpectedly, (await rig.Monitor.SampleAsync(true, Token)).Programs[0].Process);
        Assert.Single(rig.Monitor.Events, item => item.Message == "疑似异常退出");
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

    private sealed class Rig : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public FakePlatform Platform { get; } = new();
        public FakeStore Store { get; } = new();
        public FakeFiles Files { get; } = new();
        public ProgramMonitor Monitor { get; }

        public Rig() => Monitor = new(new(Files), Platform, Store, Store, Clock);

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
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _wall;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Assert.Null(_delay);
            _due = _ticks + dueTime.Ticks;
            return _delay = new Timer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        public int Launches;
        public Action? Spawn;
        public CommandResult ControlResult = new(CommandStatus.Accepted);

        public ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, CancellationToken cancellation)
        {
            var copy = new Dictionary<ProcessIdentity, uint?>(Exits);
            Exits.Clear();
            return new(true, Unreadable, Workers, copy);
        }

        public CommandResult Launch(LaunchEntry entry)
        {
            Launches++;
            Spawn?.Invoke();
            return new(CommandStatus.Accepted);
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
        public IReadOnlyList<ProgramInstallation> Programs = [Program];
        public int Saves;
        public IReadOnlyList<string> Suppressed { get; private set; } = [];
        public Task<(IReadOnlyList<ProgramInstallation> Programs, IReadOnlyList<string> SuppressedPaths, string? Notice)> LoadAsync(CancellationToken cancellation) =>
            Task.FromResult((Programs, Suppressed, (string?)null));
        public Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths, CancellationToken cancellation)
        {
            Programs = programs;
            Suppressed = suppressedPaths;
            Saves++;
            return Task.CompletedTask;
        }
        Task<IReadOnlyList<ProgramEvent>> IProgramEventStore.LoadAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ProgramEvent>>([]);
        public Task AppendAsync(ProgramEvent entry, CancellationToken cancellation) => Task.CompletedTask;
    }
}
