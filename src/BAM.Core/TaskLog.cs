namespace BAM.Core;

// Only complete UTF-8 lines. On a discontinuity, prior correlation is invalid.
public sealed record TaskLogBatch(IReadOnlyList<string> Lines, bool Discontinuity, bool CaughtUp);

public interface ITaskLogSource
{
    ITaskLogTail Follow(string path);
}

public interface ITaskLogTail
{
    ValueTask<TaskLogBatch> ReadAsync(CancellationToken cancellation);
}
