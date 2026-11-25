using BAM.Core;
using Xunit;

namespace BAM.Tests;

public class IdentityAndControlTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "bam-test-data");
    private static readonly string Worker = Path.Combine(Root, "first", "worker.exe");
    private static readonly string Other = Path.Combine(Root, "second", "worker.exe");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static readonly Capability Close = new(CapabilityKind.CloseProcess, CapabilityStatus.Available);
    private readonly ScriptDefinition _script = new(Guid.NewGuid(), "夹具", Path.Combine(Root, "entry.cmd"), [Worker]);

    [Fact]
    public void SameNameInDifferentInstallationsIsDistinct() =>
        Assert.NotEqual(new ProcessIdentity(Worker, 42, Epoch), new ProcessIdentity(Other, 42, Epoch));

    [Fact]
    public void ReusedPidIsDistinct() =>
        Assert.NotEqual(new ProcessIdentity(Worker, 42, Epoch), new ProcessIdentity(Worker, 42, Epoch.AddSeconds(1)));

    [Fact]
    public void NormalizedPathAndUtcIdentifyTheSameWindowsInstance()
    {
        var expected = new ProcessIdentity(Worker, 42, Epoch);
        var normalized = new ProcessIdentity(Path.Combine(Root.ToUpperInvariant(), "first", ".", "WORKER.EXE"),
            42, Epoch.ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected.GetHashCode(), normalized.GetHashCode());
    }

    [Fact]
    public void RelativeWorkerPathIsRejected() =>
        Assert.Throws<ArgumentException>(() => new ProcessIdentity("worker.exe", 42, Epoch));

    [Fact]
    public void UnreadableCandidateIsUnknownWhileConfirmedAbsenceIsStopped()
    {
        Assert.Equal(ProcessState.Unknown, ProcessInspection.FromCandidate(null).State);
        Assert.Equal(ProcessState.Stopped, ProcessInspection.ConfirmedAbsent.State);
    }

    [Fact]
    public void UnconfirmedOrForeignTargetCannotBeControlled()
    {
        var known = new ProcessIdentity(Worker, 42, Epoch);
        var foreign = new ProcessIdentity(Other, 42, Epoch);
        Assert.Equal(CommandStatus.Denied, Check(known, null).Status);
        Assert.Equal(CommandStatus.Denied, Check(null, known).Status);
        Assert.Equal(CommandStatus.Denied, Check(foreign, foreign).Status);
    }

    [Fact]
    public void ReplacementBetweenSelectionAndActionRejectsTheCommand()
    {
        var target = new ProcessIdentity(Worker, 42, Epoch);
        Assert.Equal(CommandStatus.IdentityChanged, Check(target, new(Worker, 42, Epoch.AddTicks(1))).Status);
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported, CommandStatus.Unsupported)]
    [InlineData(CapabilityStatus.Unavailable, CommandStatus.Denied)]
    [InlineData(CapabilityStatus.Unknown, CommandStatus.Denied)]
    [InlineData(CapabilityStatus.Available, CommandStatus.Accepted)]
    public void ControlRequiresAnAvailableCapability(CapabilityStatus availability, CommandStatus expected)
    {
        var target = new ProcessIdentity(Worker, 42, Epoch);
        Assert.Equal(expected, Check(target, target, Close with { Status = availability }).Status);
    }

    [Fact]
    public void OtherAvailableCapabilityCannotAuthorizeClosing()
    {
        var target = new ProcessIdentity(Worker, 42, Epoch);
        Assert.Equal(CommandStatus.Denied, Check(target, target, Close with { Kind = CapabilityKind.ReadResources }).Status);
    }

    private CommandResult Check(ProcessIdentity? target, ProcessIdentity? current, Capability? capability = null) =>
        CommandPolicy.CheckTarget(_script, CapabilityKind.CloseProcess, capability ?? Close, target,
            ProcessInspection.FromCandidate(current));
}
