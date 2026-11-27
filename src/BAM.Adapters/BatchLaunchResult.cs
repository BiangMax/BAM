using BAM.Core;

namespace BAM.Adapters;

public enum BatchLaunchState { Pending, Running, Unconfirmed, Failed, Skipped }

public sealed class BatchLaunchResult
{
    private volatile BatchLaunchState _observation = BatchLaunchState.Pending;
    private volatile Dispatch? _dispatch;

    internal BatchLaunchResult(ProgramInstallation program, LaunchEntry entry)
    {
        Program = program;
        Entry = entry;
    }

    public ProgramInstallation Program { get; }
    private LaunchEntry Entry { get; }
    public CommandResult Result { get; internal set; } = new(CommandStatus.Unknown);
    public BatchLaunchState State => Result.Status switch
    {
        CommandStatus.Accepted => _observation,
        CommandStatus.Unknown or CommandStatus.TimedOut => BatchLaunchState.Unconfirmed,
        CommandStatus.Denied when AlreadyRunning => BatchLaunchState.Skipped,
        _ => BatchLaunchState.Failed
    };

    internal bool AlreadyRunning { get; set; }

    internal void BeginObservation(ProcessIdentity? previousWorker, DateTimeOffset launchStarted, long lastSample) =>
        _dispatch = new(previousWorker, launchStarted, lastSample);

    // Only the pending script dispatch that owns this row can provide an observation.
    internal void Observe(ProgramStatus status, InstallationCatalog catalog, long sampleNumber, bool expired, bool accepted)
    {
        if (_dispatch is not { } dispatch || sampleNumber <= dispatch.LastSample || _observation != BatchLaunchState.Pending ||
            !ReferenceEquals(status.Installation, Program)) return;
        if (expired)
        {
            _observation = BatchLaunchState.Unconfirmed;
            return;
        }
        if (status.Worker is not { } worker) return;
        if (status.HasNewWorker(dispatch.PreviousWorker) && worker.Identity.CreatedAtUtc >= dispatch.Started &&
            EntryStillMatches(catalog))
            _observation = BatchLaunchState.Running;
        else if (accepted)
            _observation = BatchLaunchState.Unconfirmed;
    }

    private bool EntryStillMatches(InstallationCatalog catalog)
    {
        try { return catalog.HasWorkerBinding(Program) && catalog.Availability(Program, out var current) is null && current == Entry; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private sealed record Dispatch(ProcessIdentity? PreviousWorker, DateTimeOffset Started, long LastSample);
}
