namespace BAM.Core;

public enum CapabilityKind { ReadResources, ActivateWindow, CloseProcess, ForceTerminate, StartGameTask, StopGameTask }
public enum CapabilityStatus { Unknown, Unsupported, Unavailable, Available }
public sealed record Capability(CapabilityKind Kind, CapabilityStatus Status, string? Reason = null);

public interface IScriptAdapter
{
    ValueTask<Capability> ProbeAsync(ScriptDefinition script, CapabilityKind kind, CancellationToken cancellation);
}

public sealed class CapabilityProbe
{
    public async ValueTask<Capability> InspectAsync(
        IScriptAdapter adapter, ScriptDefinition script, CapabilityKind kind, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var result = await adapter.ProbeAsync(script, kind, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return result.Kind == kind ? result : new(kind, CapabilityStatus.Unknown, "适配器返回了其他能力。");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            return new(kind, CapabilityStatus.Unavailable, $"适配器探测失败 ({error.GetType().Name})。");
        }
    }
}
