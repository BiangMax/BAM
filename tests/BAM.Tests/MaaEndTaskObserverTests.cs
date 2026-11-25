using System.Globalization;
using BAM.Adapters;
using BAM.Core;
using Xunit;

namespace BAM.Tests;

public sealed class MaaEndTaskObserverTests
{
    [Theory]
    [InlineData("Succeeded", "completed", GameTaskState.Completed)]
    [InlineData("Failed", "recognition_timeout", GameTaskState.Failed)]
    [InlineData("Failed", "cancelled", GameTaskState.Cancelled)]
    [InlineData("Succeeded", "cancelled", GameTaskState.Cancelled)]
    [InlineData("Failed", null, GameTaskState.Unknown)]
    [InlineData("Failed", "unknown", GameTaskState.Unknown)]
    [InlineData("Succeeded", null, GameTaskState.Unknown)]
    public async Task TerminalReasonDeterminesOutcome(string kind, string? reason, GameTaskState expected)
    {
        var rig = new Rig();
        await rig.Poll();
        Assert.Equal(GameTaskState.Running, (await rig.Poll([rig.Event("Starting")])).Task);
        var terminal = rig.Event(kind, reason);
        var result = await rig.Poll([terminal, terminal]);
        Assert.Equal(expected, result.Task);
        Assert.Equal(ProcessState.Running, result.Process);
    }

    [Fact]
    public async Task IgnoresHistoricalAndForeignStartsAndRequiresTheSameTaskForTerminal()
    {
        var rig = new Rig();
        var old = rig.Event("Starting");
        await rig.Poll();
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([old, rig.Event("Starting", pid: 18),
            rig.Event("Succeeded", "completed")])).Task);
        var start = rig.Event("Starting", id: 2);
        Assert.Equal(GameTaskState.Running, (await rig.Poll([start, start])).Task);
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Succeeded", "completed", id: 2, scope: "another")])).Task);
        Assert.Equal(GameTaskState.Completed, (await rig.Poll([rig.Event("Succeeded", "completed", id: 2)])).Task);
    }

    [Fact]
    public async Task DiscontinuityAndReusedPidInvalidateStartEvidence()
    {
        var rig = new Rig();
        await rig.Poll();
        await rig.Poll([rig.Event("Starting")]);
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll(discontinuity: true)).Task);
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Succeeded", "completed")])).Task);
        await rig.Poll([rig.Event("Starting", id: 2)]);
        rig.Row = rig.Row with { Worker = rig.Row.Worker! with
        {
            Identity = new(rig.ImagePath, 17, rig.Clock.GetUtcNow())
        } };
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Succeeded", "completed", id: 2)])).Task);
        Assert.Equal(2, rig.Source.FollowCount);
    }

    [Fact]
    public async Task MissingTerminalExpiresToUnknownAndReadFailureLeavesProcessStateIntact()
    {
        var rig = new Rig();
        await rig.Poll();
        await rig.Poll([rig.Event("Starting")]);
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll()).Task);
        Assert.Equal(GameTaskState.Completed, (await rig.Poll([rig.Event("Succeeded", "completed")])).Task);
        rig.Source.ReadError = new IOException("fixture error");
        var unavailable = await rig.Poll();
        Assert.Equal(GameTaskState.Unknown, unavailable.Task);
        Assert.Equal(ProcessState.Running, unavailable.Process);
        Assert.DoesNotContain("fixture error", unavailable.TaskHint);
    }

    [Fact]
    public async Task VersionGateAndUnconfirmedWorkersNeverOpenTheLog()
    {
        var rig = new Rig();
        var row = rig.Row;
        rig.Row = row with { Metadata = row.Metadata with { ResourceVersion = "v9" } };
        Assert.Equal(GameTaskState.Unsupported, (await rig.Poll()).Task);
        rig.Row = row with { Metadata = row.Metadata with { ResourceVersion = null } };
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll()).Task);
        rig.Row = row with { Process = ProcessState.Unknown, Worker = null };
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll()).Task);
        Assert.Equal(0, rig.Source.FollowCount);
    }

    [Fact]
    public async Task LosingVersionEvidenceInvalidatesThePreviousObservation()
    {
        var rig = new Rig();
        var row = rig.Row;
        await rig.Poll();
        await rig.Poll([rig.Event("Starting")]);
        rig.Row = row with { Metadata = row.Metadata with { ResourceVersion = null } };
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll()).Task);
        rig.Row = row;
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Succeeded", "completed")])).Task);
        Assert.Equal(2, rig.Source.FollowCount);
    }

    [Fact]
    public async Task OnlyCompleteBacklogIsPublishedAndOverlappingTasksStayUnknown()
    {
        var rig = new Rig();
        await rig.Poll();
        var start = rig.Event("Starting");
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([start], caughtUp: false)).Task);
        Assert.Equal(GameTaskState.Running, (await rig.Poll()).Task);
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Starting", id: 2)])).Task);
        Assert.Equal(GameTaskState.Unknown, (await rig.Poll([rig.Event("Succeeded", "completed"),
            rig.Event("Succeeded", "completed", id: 2)])).Task);
        Assert.Equal(GameTaskState.Running, (await rig.Poll([rig.Event("Starting", id: 3)])).Task);
    }

    [Fact]
    public async Task InstallationScopesDoNotShareTaskEvidence()
    {
        var rig = new Rig();
        await rig.Poll();
        await rig.Poll([rig.Event("Starting")]);
        var another = rig.Row with { Installation = rig.Row.Installation with { Id = Guid.NewGuid() } };
        var results = await rig.Observer.ObserveAsync(rig.Frame([rig.Row, another]), TestContext.Current.CancellationToken);
        Assert.Equal([GameTaskState.Running, GameTaskState.Unknown], results.Programs.Select(row => row.Task));
        Assert.Equal(2, rig.Source.FollowCount);
    }

    private sealed class Rig
    {
        public FixtureClock Clock { get; } = new();
        public FixtureSource Source { get; } = new();
        public string ImagePath { get; } = Path.GetFullPath(Path.Combine("fixture", "MaaEnd.exe"));
        public ProgramStatus Row { get; set; }
        public MaaEndTaskObserver Observer { get; }

        public Rig()
        {
            var folder = Path.GetDirectoryName(ImagePath)!;
            var installation = new ProgramInstallation(Guid.NewGuid(), "fixture", ProgramKind.MaaEnd,
                new(ImagePath, ImagePath, folder, ""), folder, [ImagePath]);
            Row = new(installation, new("2.11.2", "", null, true, null, ProductVersion: "2.11.2",
                ResourceVersion: "v3.3.0-beta.2"), ProcessState.Running,
                new(new(ImagePath, 17, Clock.GetUtcNow().AddMinutes(-5)), null, null, null, null, false),
                null, null, null, null, new HashSet<ProgramAction>());
            Observer = new(Source, Clock);
        }

        public string Event(string kind, string? reason = null, long id = 1, int pid = 17, string scope = "synthetic")
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var time = Clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var terminal = reason is null ? "" : $",\"reason\":\"{reason}\"";
            return $"[{time}][INF][Px{pid}][Tx5][Utils/EventDispatcher.hpp][L65][MaaNS::EventDispatcher::notify] " +
                $"!!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.{kind}] " +
                $"[details={{\"entry\":\"FixtureEntry\",\"task_id\":{id},\"uuid\":\"{scope}\"{terminal}}}]";
        }

        public ProgramFrame Frame(IReadOnlyList<ProgramStatus> programs) =>
            new(Clock.GetUtcNow(), TimeSpan.Zero, programs, null, null, 0, 0, programs.Count, 0);

        public async Task<ProgramStatus> Poll(IReadOnlyList<string>? lines = null, bool discontinuity = false, bool caughtUp = true)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Source.Next = new(lines ?? [], discontinuity, caughtUp);
            var frame = await Observer.ObserveAsync(Frame([Row]), TestContext.Current.CancellationToken);
            return Assert.Single(frame.Programs);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FixtureSource : ITaskLogSource, ITaskLogTail
    {
        public TaskLogBatch Next { get; set; } = new([], false, true);
        public Exception? ReadError { get; set; }
        public int FollowCount { get; private set; }

        public ITaskLogTail Follow(string path)
        {
            Assert.Equal(Path.Combine(Path.GetFullPath("fixture"), "debug", "maafw.log"), path);
            FollowCount++;
            return this;
        }

        public ValueTask<TaskLogBatch> ReadAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ReadError is not null) throw ReadError;
            var current = Next;
            Next = new([], false, true);
            return ValueTask.FromResult(current);
        }
    }
}
