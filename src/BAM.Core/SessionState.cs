namespace BAM.Core;

public enum ProcessState { Unknown, Stopped, Starting, Running, Stopping, ExitedUnexpectedly }
public enum WindowState { Unknown, NoVisibleWindow, Visible, Minimized }
public enum GameTaskState { Unknown, Unsupported, Idle, Starting, Running, Stopping, Completed, Cancelled, Failed }
public enum RemoteCommandState { Unknown, Queued, Offered, ExecutionConfirmed, Reported, TimedOut }

public sealed record Observation<T>(Guid SessionId, DateTimeOffset ObservedAt, string Source, T Value, Guid? CommandId = null)
    where T : struct, Enum;

// One stream per axis and observation scope. Reported commands are scoped by command ID.
public sealed class ObservationStream<T> where T : struct, Enum
{
    private readonly Guid _sessionId;
    private readonly DateTimeOffset _openedAt;
    private readonly T _unknown;
    private readonly Guid? _commandId;
    private readonly object _sync = new();
    private Observation<T>? _latest;

    internal ObservationStream(Guid sessionId, DateTimeOffset openedAt, T unknown, Guid? commandId = null)
    {
        (_sessionId, _openedAt, _unknown) = (sessionId, openedAt, unknown);
        _commandId = commandId;
    }

    public T Value
    {
        get { lock (_sync) return _latest?.Value ?? _unknown; }
    }

    public Observation<T>? Latest
    {
        get { lock (_sync) return _latest; }
    }

    public bool Apply(Observation<T> observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Source);
        if (observation.SessionId != _sessionId || observation.CommandId != _commandId || observation.ObservedAt < _openedAt)
            return false;

        lock (_sync)
        {
            if (_latest is not null && observation.ObservedAt <= _latest.ObservedAt)
            {
                if (observation.ObservedAt < _latest.ObservedAt ||
                    observation.Value.Equals(_latest.Value) || _latest.Value.Equals(_unknown))
                    return false;

                _latest = observation with { Value = _unknown, Source = "conflicting-evidence" };
                return true;
            }

            _latest = observation;
            return true;
        }
    }
}

public sealed class SessionState
{
    public SessionState(TimeProvider clock)
    {
        var opened = clock.GetUtcNow();
        Process = new(Id, opened, ProcessState.Unknown);
        Window = new(Id, opened, WindowState.Unknown);
        GameTask = new(Id, opened, GameTaskState.Unknown);
        _openedAt = opened;
    }

    private readonly DateTimeOffset _openedAt;
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset OpenedAt => _openedAt;
    public ObservationStream<ProcessState> Process { get; }
    public ObservationStream<WindowState> Window { get; }
    public ObservationStream<GameTaskState> GameTask { get; }

    // The caller owns one stream per command; it expires with the owning session.
    public ObservationStream<RemoteCommandState> CreateCommandStream(Guid commandId)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
        return new(Id, _openedAt, RemoteCommandState.Unknown, commandId);
    }
}
