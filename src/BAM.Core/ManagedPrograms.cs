using System.Text.Json.Serialization;

namespace BAM.Core;

public enum ProgramKind { Generic, MaaEnd, Maa, BBchannel }
public enum ProgramAction { Start, Activate, Close, Restart, Force, StartWithEmulator }
public enum EventSeverity { Information, Warning, Error }

public sealed record LaunchEntry(string OriginalPath, string TargetPath, string WorkingDirectory,
    string Arguments, string? IconPath = null, int IconIndex = 0, bool RunAs = false, string? ResolvedTarget = null)
{
    [JsonIgnore]
    public bool IsBatch => Path.GetExtension(TargetPath) is { } extension &&
        (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public bool HasSafeBatchArguments =>
        TargetPath.IndexOfAny(['%', '!', '^', '&', '|', '<', '>', '(', ')', '"']) < 0 &&
        Arguments.IndexOfAny(['%', '!', '^', '&', '|', '<', '>', '(', ')']) < 0 &&
        Arguments.Count(character => character == '"') % 2 == 0 && !Arguments.Any(char.IsControl);
}

public sealed record EmulatorStartup(LaunchEntry Entry, int WaitSeconds)
{
    public const int DefaultWaitSeconds = 30;
    public const int MaximumWaitSeconds = 180;
}

public sealed record EmulatorMonitoring(string ImagePath, int? InstanceNumber = null, string? WorkerImagePath = null);

public sealed record EmulatorResourceUsage(double? Cpu, double? WorkingSetMiB, string Hint)
{
    public DateTimeOffset? SampledAt { get; init; }
}

public sealed record ProgramInstallation(Guid Id, string Name, ProgramKind Kind, LaunchEntry Entry,
    string InstallDirectory, string[] WorkerPaths)
{
    public bool InheritShortcutArguments { get; init; }
    public bool InheritShortcutDirectory { get; init; }
    public EmulatorStartup? Emulator { get; init; }
    public EmulatorMonitoring? EmulatorMonitor { get; init; }

    [JsonIgnore]
    public bool SupportsEmulatorStartup => Kind is ProgramKind.MaaEnd or ProgramKind.BBchannel;

    [JsonIgnore]
    public ScriptDefinition Definition => new(Id, Name, Entry.TargetPath, WorkerPaths);
}

public sealed record ProgramMetadata(string Version, string VersionDetail, byte[]? Icon, bool EntryAvailable,
    string? Issue, bool WorkerBindingValid = true, string? ProductVersion = null, string? ResourceVersion = null,
    LaunchEntry? EffectiveEntry = null);

public sealed record ProgramWindow(nint Handle, string Title, WindowState State, bool Responding);

public sealed record WorkerSnapshot(ProcessIdentity Identity, TimeSpan? CpuTime, long? WorkingSet,
    ProgramWindow? Window, bool? WindowControlAllowed, bool CanTerminate);

public sealed record ProcessScan(bool Complete, IReadOnlySet<string> UnreadableNames,
    IReadOnlyList<WorkerSnapshot> Workers, IReadOnlyDictionary<ProcessIdentity, uint?> Exits);

public enum PairedStartupPhase { StartingEmulator, Waiting, StartingScript }
public sealed record PairedStartupProgress(PairedStartupPhase Phase, int? RemainingSeconds = null);

public sealed record ProgramStatus(ProgramInstallation Installation, ProgramMetadata Metadata, ProcessState Process,
    WorkerSnapshot? Worker, double? Cpu, double? WorkingSetMiB, TimeSpan? Uptime, string? Issue,
    IReadOnlySet<ProgramAction> Actions, bool LaunchConfirmationTimedOut = false,
    GameTaskState Task = GameTaskState.Unsupported, string? TaskHint = null, EmulatorResourceUsage? EmulatorUsage = null,
    PairedStartupProgress? PairedStartup = null);

public sealed record ProgramFrame(DateTimeOffset ObservedAt, TimeSpan Elapsed, IReadOnlyList<ProgramStatus> Programs,
    double? Cpu, double? WorkingSetMiB, int CpuCoverage, int MemoryCoverage, int ConfirmedWorkers, int UnknownPrograms,
    ProgramEventHistory? EventHistory = null);

public sealed record ProgramEvent(DateTimeOffset Time, Guid? InstallationId, string Source, EventSeverity Severity,
    string Message, string Detail);

public sealed record ProgramEventHistory(IReadOnlyList<ProgramEvent> Entries, long OlderEntriesExcluded = 0)
{
    public const int Capacity = 2000;
}

public sealed class ProgramEventBuffer
{
    private readonly Queue<ProgramEvent> _entries = new(ProgramEventHistory.Capacity);
    private ProgramEventHistory? _snapshot;
    private long _olderEntriesExcluded;

    public void Restore(ProgramEventHistory history)
    {
        _entries.Clear();
        _olderEntriesExcluded = history.OlderEntriesExcluded;
        _snapshot = null;
        foreach (var entry in history.Entries.Reverse()) Add(entry);
    }

    public void Add(ProgramEvent entry)
    {
        if (_entries.Count == ProgramEventHistory.Capacity)
        {
            _entries.Dequeue();
            _olderEntriesExcluded++;
        }
        _entries.Enqueue(entry);
        _snapshot = null;
    }

    public ProgramEventHistory Snapshot() => _snapshot ??= new(_entries.Reverse().ToArray(), _olderEntriesExcluded);
}

public interface IProgramFiles
{
    IEnumerable<string> FindShortcuts(CancellationToken cancellation);
    LaunchEntry ReadEntry(string path);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string? ReadSmallText(string path);
    string? ReadProductVersion(string path);
    byte[]? ReadIcon(string path, int index);
    string ResolveImagePath(string path) => path;
}

public interface IProgramPlatform
{
    ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, CancellationToken cancellation);
    ProcessScan ScanEmulators(IReadOnlySet<string> paths, CancellationToken cancellation) =>
        new(false, new HashSet<string>(), [], new Dictionary<ProcessIdentity, uint?>());
    string[]? ReadEmulatorArguments(ProcessIdentity identity, CancellationToken cancellation) => null;
    IReadOnlyDictionary<ProcessIdentity, string[]?> ReadEmulatorArgumentsBatch(IReadOnlyList<ProcessIdentity> identities,
        CancellationToken cancellation) => identities.ToDictionary(identity => identity, identity => ReadEmulatorArguments(identity, cancellation));
    // Accepted means the OS accepted the request. Unknown means it may have been submitted.
    // Denied, Unsupported and Failed indicate a definite rejection before dispatch.
    CommandResult Launch(LaunchEntry entry);
    CommandResult Control(ScriptDefinition script, ProgramAction action, ProcessIdentity identity, ProgramWindow? window);
}

public interface IProgramStore
{
    Task<ProgramStoreLoad> LoadAsync(CancellationToken cancellation);
    Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths, CancellationToken cancellation);
}

public sealed record ProgramStoreLoad(IReadOnlyList<ProgramInstallation> Programs,
    IReadOnlyList<string> SuppressedPaths, string? Notice, bool Readable = true);

public interface IProgramEventStore
{
    Task<ProgramEventHistory> LoadAsync(CancellationToken cancellation);
    Task AppendAsync(ProgramEvent entry, CancellationToken cancellation);
}
