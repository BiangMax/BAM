using System.Collections.Frozen;

namespace BAM.Core;

public sealed class ScriptDefinition
{
    public ScriptDefinition(Guid id, string name, string launchPath, IEnumerable<string> workerPaths)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        LaunchPath = ProcessIdentity.NormalizePath(launchPath);
        WorkerPaths = workerPaths.Select(ProcessIdentity.NormalizePath).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string LaunchPath { get; }
    public IReadOnlySet<string> WorkerPaths { get; }
}
