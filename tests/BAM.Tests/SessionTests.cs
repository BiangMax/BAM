using BAM.Core;
using Xunit;

namespace BAM.Tests;

public class SessionTests
{
    private readonly FrozenClock _clock = new();
    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    [Fact]
    public void PreviousSessionCannotOverwriteFreshStateEvenWithLaterTimestamp()
    {
        var previous = new SessionState(_clock);
        var current = new SessionState(_clock);
        var time = _clock.GetUtcNow();
        current.GameTask.Apply(new(current.Id, time, "log", GameTaskState.Running));
        Assert.False(current.GameTask.Apply(new(previous.Id, time.AddHours(1), "log", GameTaskState.Failed)));
        Assert.Equal(GameTaskState.Running, current.GameTask.Value);
    }

    [Fact]
    public void EntryFromBeforeSessionIsIgnored()
    {
        var session = new SessionState(_clock);
        Assert.False(session.Process.Apply(new(session.Id, _clock.GetUtcNow().AddTicks(-1), "scan", ProcessState.Running)));
        Assert.Null(session.Process.Latest);
    }

    [Fact]
    public void DuplicateAndOutOfOrderObservationsHaveNoEffect()
    {
        var session = new SessionState(_clock);
        var time = _clock.GetUtcNow().AddSeconds(2);
        var signal = new Observation<GameTaskState>(session.Id, time, "log", GameTaskState.Completed);
        Assert.True(session.GameTask.Apply(signal));
        Assert.False(session.GameTask.Apply(signal));
        Assert.False(session.GameTask.Apply(signal with { ObservedAt = time.AddSeconds(-1), Value = GameTaskState.Running }));
        Assert.Equal(signal, session.GameTask.Latest);
    }

    [Fact]
    public void EqualTimeConflictStaysUnknownUntilNewerEvidence()
    {
        var session = new SessionState(_clock);
        var started = new Observation<GameTaskState>(session.Id, _clock.GetUtcNow(), "log", GameTaskState.Running);
        var failed = started with { Source = "callback", Value = GameTaskState.Failed };
        session.GameTask.Apply(started);
        Assert.True(session.GameTask.Apply(failed));
        Assert.False(session.GameTask.Apply(started));
        Assert.Equal(GameTaskState.Unknown, session.GameTask.Value);
        Assert.True(session.GameTask.Apply(failed with { ObservedAt = failed.ObservedAt.AddTicks(1) }));
        Assert.Equal(GameTaskState.Failed, session.GameTask.Value);
    }

    [Fact]
    public void LiveProcessAndResponsiveWindowCannotStartGameTask()
    {
        var session = new SessionState(_clock);
        session.Process.Apply(new(session.Id, _clock.GetUtcNow(), "scan", ProcessState.Running));
        session.Window.Apply(new(session.Id, _clock.GetUtcNow(), "window", WindowState.Visible));
        Assert.Equal(GameTaskState.Unknown, session.GameTask.Value);
    }

    [Fact]
    public void CommandReportIsScopedAndCannotCompleteGameTask()
    {
        var session = new SessionState(_clock);
        var id = Guid.NewGuid();
        var command = session.CreateCommandStream(id);
        var report = new Observation<RemoteCommandState>(session.Id, _clock.GetUtcNow(), "report", RemoteCommandState.Reported, id);
        Assert.False(command.Apply(report with { CommandId = Guid.NewGuid() }));
        Assert.True(command.Apply(report));
        Assert.Equal(RemoteCommandState.Reported, command.Value);
        Assert.Equal(GameTaskState.Unknown, session.GameTask.Value);
    }
}
