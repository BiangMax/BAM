using BAM.Core;
using Xunit;

namespace BAM.Tests;

public class CapabilityProbeTests
{
    private static readonly string Entry = Path.Combine(Path.GetTempPath(), "test-entry.exe");
    private readonly ScriptDefinition _script = new(Guid.NewGuid(), "夹具", Entry, [Entry]);
    private readonly CapabilityProbe _probe = new();

    private sealed class Adapter(Func<CapabilityKind, Capability> probe) : IScriptAdapter
    {
        public ValueTask<Capability> ProbeAsync(ScriptDefinition script, CapabilityKind kind, CancellationToken cancellation) =>
            ValueTask.FromResult(probe(kind));
    }

    [Fact]
    public async Task FaultInOneAdapterLeavesOtherProbeAvailable()
    {
        var broken = new Adapter(_ => throw new IOException());
        var healthy = new Adapter(kind => new(kind, CapabilityStatus.Available));
        var kind = CapabilityKind.ReadResources;
        var results = await Task.WhenAll(
            _probe.InspectAsync(broken, _script, kind, TestContext.Current.CancellationToken).AsTask(),
            _probe.InspectAsync(healthy, _script, kind, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CapabilityStatus.Unavailable, results[0].Status);
        Assert.NotNull(results[0].Reason);
        Assert.Equal(CapabilityStatus.Available, results[1].Status);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutCallingAdapter()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var adapter = new Adapter(_ => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _probe.InspectAsync(adapter, _script, CapabilityKind.ReadResources, cancelled.Token));
    }

    [Fact]
    public async Task MismatchedProbeResponseIsUnknown()
    {
        var adapter = new Adapter(_ => new(CapabilityKind.ReadResources, CapabilityStatus.Available));
        var result = await _probe.InspectAsync(adapter, _script, CapabilityKind.ForceTerminate, TestContext.Current.CancellationToken);
        Assert.Equal(CapabilityStatus.Unknown, result.Status);
    }
}
