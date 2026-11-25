namespace BAM.Core;

public sealed class ProcessInspection
{
    private ProcessInspection(ProcessState state, ProcessIdentity? identity) =>
        (State, Identity) = (state, identity);

    public ProcessState State { get; }
    public ProcessIdentity? Identity { get; }

    // A completed enumeration can establish absence. An unreadable candidate cannot.
    public static ProcessInspection ConfirmedAbsent { get; } = new(ProcessState.Stopped, null);
    public static ProcessInspection Unavailable { get; } = new(ProcessState.Unknown, null);
    public static ProcessInspection FromCandidate(ProcessIdentity? identity) =>
        identity is null ? Unavailable : new(ProcessState.Running, identity);
}
