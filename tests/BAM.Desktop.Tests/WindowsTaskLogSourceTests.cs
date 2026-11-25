using System.IO;
using System.Text;
using BAM.Adapters;
using BAM.Core;
using BAM.Infrastructure;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class WindowsTaskLogSourceTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("BamSyntheticTaskLog-");
    private string Log => Path.Combine(_directory.FullName, "maafw.log");

    [Fact]
    public async Task SyntheticFrameworkFileFeedsOnlyCurrentCorrelatedTask()
    {
        var path = _directory.CreateSubdirectory("debug");
        var log = Path.Combine(path.FullName, "maafw.log");
        var image = Path.Combine(_directory.FullName, "MaaEnd.exe");
        var installation = new ProgramInstallation(Guid.NewGuid(), "Fixture", ProgramKind.MaaEnd,
            new(image, image, _directory.FullName, ""), _directory.FullName, [image]);
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var row = new ProgramStatus(installation, new("2.11.2", "", null, true, null, ProductVersion: "2.11.2",
            ResourceVersion: "v3.3.0-beta.2"), ProcessState.Running,
            new(new(image, 17, created), null, null, null, null, false), null, null, null, null, new HashSet<ProgramAction>());
        var clock = new FixtureClock(created.AddSeconds(2));
        var observer = new MaaEndTaskObserver(new WindowsTaskLogSource(), clock);

        await File.WriteAllTextAsync(log, Line("01", "Starting"), Token);
        async Task<GameTaskState> ReadTask()
        {
            var frame = new ProgramFrame(clock.Now, TimeSpan.Zero, [row], null, null, 0, 0, 1, 0);
            return Assert.Single((await observer.ObserveAsync(frame, Token)).Programs).Task;
        }
        Assert.Equal(GameTaskState.Unknown, await ReadTask());
        clock.Now = created.AddSeconds(4);
        await File.AppendAllTextAsync(log, Line("03", "Succeeded", "completed"), Token);
        Assert.Equal(GameTaskState.Unknown, await ReadTask());
        clock.Now = created.AddSeconds(6);
        await File.AppendAllTextAsync(log, Line("05", "Starting"), Token);
        Assert.Equal(GameTaskState.Running, await ReadTask());
        clock.Now = created.AddSeconds(8);
        await File.AppendAllTextAsync(log, Line("07", "Failed", "cancelled"), Token);
        Assert.Equal(GameTaskState.Cancelled, await ReadTask());
    }

    private static string Line(string seconds, string kind, string? reason = null)
    {
        var reasonField = reason is null ? "" : $",\"reason\":\"{reason}\"";
        return $"[2026-01-01 00:00:{seconds}.000][INF][Px17][Tx5][Utils/EventDispatcher.hpp][L65][MaaNS::EventDispatcher::notify] " +
            $"!!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.{kind}] " +
            $"[details={{\"entry\":\"合成任务\",\"task_id\":1,\"uuid\":\"fixture\"{reasonField}}}]\n";
    }

    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task SkipsOldBytesAndKeepsSplitUtf8UntilTheLineIsComplete()
    {
        await File.WriteAllTextAsync(Log, "previous record\nan old partial", Token);
        var tail = new WindowsTaskLogSource().Follow(Log);
        var first = await tail.ReadAsync(Token);
        Assert.True(first.Discontinuity);
        Assert.Empty(first.Lines);
        await using var writer = new FileStream(Log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var data = Encoding.UTF8.GetBytes(" still old\n任务\r\n");
        var split = " still old\n".Length + 1;
        await writer.WriteAsync(data.AsMemory(0, split), Token);
        await writer.FlushAsync(Token);
        Assert.Empty((await tail.ReadAsync(Token)).Lines);
        await writer.WriteAsync(data.AsMemory(split), Token);
        await writer.FlushAsync(Token);
        var next = await tail.ReadAsync(Token);
        Assert.False(next.Discontinuity);
        Assert.Equal("任务", Assert.Single(next.Lines));
    }

    [Fact]
    public async Task RotationAndCopyTruncateWithRegrowthNeverReplayExistingContent()
    {
        await File.WriteAllTextAsync(Log, "first generation\n", Token);
        var tail = new WindowsTaskLogSource().Follow(Log);
        await tail.ReadAsync(Token);
        File.Move(Log, Log + ".bak");
        await File.WriteAllTextAsync(Log, "replacement already contains records\n", Token);
        await AssertReattached(tail);
        await File.AppendAllTextAsync(Log, "after rotation\n", Token);
        Assert.Equal("after rotation", Assert.Single((await tail.ReadAsync(Token)).Lines));
        await File.WriteAllTextAsync(Log, new string('x', 100) + "\n", Token);
        await AssertReattached(tail);
        await File.AppendAllTextAsync(Log, "after truncate\n", Token);
        Assert.Equal("after truncate", Assert.Single((await tail.ReadAsync(Token)).Lines));
    }

    [Fact]
    public async Task CancellationLeavesUnreadBytesAvailableAndOversizeRecordsSignalLoss()
    {
        await File.WriteAllTextAsync(Log, "", Token);
        var tail = new WindowsTaskLogSource().Follow(Log);
        await tail.ReadAsync(Token);
        await File.AppendAllTextAsync(Log, "unread\n", Token);
        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tail.ReadAsync(cancelled).AsTask());
        Assert.Equal("unread", Assert.Single((await tail.ReadAsync(Token)).Lines));

        await File.AppendAllTextAsync(Log, new string('x', 140 * 1024) + "\n", Token);
        var large = await tail.ReadAsync(Token);
        Assert.True(large.Discontinuity);
        Assert.False(large.CaughtUp);
        Assert.Empty(large.Lines);
        Assert.True((await tail.ReadAsync(Token)).CaughtUp);
    }

    private static async Task AssertReattached(ITaskLogTail tail)
    {
        var batch = await tail.ReadAsync(Token);
        Assert.True(batch.Discontinuity);
        Assert.Empty(batch.Lines);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
