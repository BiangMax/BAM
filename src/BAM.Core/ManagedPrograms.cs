using System.Text.Json.Serialization;

namespace BAM.Core;

public enum ProgramKind { Generic, MaaEnd, Maa, BBchannel }
public enum ProgramAction { Start, Activate, Close, Restart, Force }
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

public sealed record ProgramInstallation(Guid Id, string Name, ProgramKind Kind, LaunchEntry Entry,
    string InstallDirectory, string[] WorkerPaths)
{
    [JsonIgnore]
    public ScriptDefinition Definition => new(Id, Name, Entry.TargetPath, WorkerPaths);
}

public sealed record ProgramMetadata(string Version, string VersionDetail, byte[]? Icon, bool EntryAvailable,
    string? Issue, bool WorkerBindingValid = true, string? ProductVersion = null, string? ResourceVersion = null);

public sealed record ProgramWindow(nint Handle, string Title, WindowState State, bool Responding);

public sealed record WorkerSnapshot(ProcessIdentity Identity, TimeSpan? CpuTime, long? WorkingSet,
    ProgramWindow? Window, bool? WindowControlAllowed, bool CanTerminate);

public sealed record ProcessScan(bool Complete, IReadOnlySet<string> UnreadableNames,
    IReadOnlyList<WorkerSnapshot> Workers, IReadOnlyDictionary<ProcessIdentity, uint?> Exits);

public sealed record ProgramStatus(ProgramInstallation Installation, ProgramMetadata Metadata, ProcessState Process,
    WorkerSnapshot? Worker, double? Cpu, double? WorkingSetMiB, TimeSpan? Uptime, string? Issue,
    IReadOnlySet<ProgramAction> Actions, bool LaunchConfirmationTimedOut = false,
    GameTaskState Task = GameTaskState.Unsupported, string? TaskHint = null);

public sealed record ProgramFrame(DateTimeOffset ObservedAt, TimeSpan Elapsed, IReadOnlyList<ProgramStatus> Programs,
    double? Cpu, double? WorkingSetMiB, int CpuCoverage, int MemoryCoverage, int ConfirmedWorkers, int UnknownPrograms);

public sealed record ProgramEvent(DateTimeOffset Time, Guid? InstallationId, string Source, EventSeverity Severity,
    string Message, string Detail);

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
    CommandResult Launch(LaunchEntry entry);
    CommandResult Control(ScriptDefinition script, ProgramAction action, ProcessIdentity identity, ProgramWindow? window);
}

public interface IProgramStore
{
    Task<(IReadOnlyList<ProgramInstallation> Programs, IReadOnlyList<string> SuppressedPaths, string? Notice)> LoadAsync(CancellationToken cancellation);
    Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths, CancellationToken cancellation);
}

public interface IProgramEventStore
{
    Task<IReadOnlyList<ProgramEvent>> LoadAsync(CancellationToken cancellation);
    Task AppendAsync(ProgramEvent entry, CancellationToken cancellation);
}
