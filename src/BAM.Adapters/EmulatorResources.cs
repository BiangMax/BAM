using System.Globalization;
using BAM.Core;

namespace BAM.Adapters;

internal sealed class EmulatorResources(IProgramPlatform platform, TimeProvider clock)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] InstanceOptions = ["-v", "--index", "--instance", "--vm-index"];
    private static readonly EmulatorResourceUsage Unavailable = new(null, null, "无法确认模拟器进程归属或读取占用。");
    private static readonly EmulatorResourceUsage CheckingInstance = new(null, null, "正在核对模拟器实例编号。");
    private Dictionary<ProcessIdentity, (long Stamp, TimeSpan Cpu)> _previous = [];
    private readonly Dictionary<ProcessIdentity, int?> _instances = [];
    private Task<Dictionary<ProcessIdentity, int?>>? _lookup;
    private long? _lookupAt;

    public IReadOnlyDictionary<Guid, EmulatorResourceUsage> Sample(IReadOnlyList<ProgramInstallation> programs, bool readResources,
        long stamp, CancellationToken cancellation)
    {
        var configured = programs.Where(program => program.EmulatorMonitor is not null).ToArray();
        var result = new Dictionary<Guid, EmulatorResourceUsage>();
        foreach (var program in configured)
            result[program.Id] = readResources ? Unavailable : new(null, null, "资源采样已暂停。");
        if (!readResources || configured.Length == 0)
        {
            _previous.Clear();
            _instances.Clear();
            return result;
        }

        var bindings = configured.Where(Valid).SelectMany(program => Images(program.EmulatorMonitor!)
            .Select(path => new Binding(program.Id, path, program.EmulatorMonitor!.InstanceNumber))).ToArray();
        var paths = bindings.Select(binding => binding.Path).ToHashSet(Paths);
        if (paths.Count == 0) return result;
        ProcessScan scan;
        try { scan = platform.ScanEmulators(paths, cancellation); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _previous.Clear();
            return result;
        }
        if (!scan.Complete)
        {
            _previous.Clear();
            return result;
        }

        var numbered = bindings.Where(binding => binding.Instance is not null).Select(binding => binding.Path).ToHashSet(Paths);
        UpdateInstanceNumbers(scan.Workers.Where(worker => numbered.Contains(worker.Identity.ImagePath))
            .Select(worker => worker.Identity).ToHashSet(), stamp, cancellation);
        var cpu = new Dictionary<ProcessIdentity, double>();
        Dictionary<ProcessIdentity, (long Stamp, TimeSpan Cpu)> next = [];
        foreach (var worker in scan.Workers)
        {
            if (worker.CpuTime is not { } total) continue;
            if (_previous.TryGetValue(worker.Identity, out var previous) &&
                ProgramMonitor.CalculateCpu(total - previous.Cpu, clock.GetElapsedTime(previous.Stamp, stamp), Environment.ProcessorCount) is { } value)
                cpu[worker.Identity] = value;
            next[worker.Identity] = (stamp, total);
        }
        _previous = next;

        var candidates = scan.Workers.ToLookup(worker => worker.Identity.ImagePath, Paths);
        var byPath = bindings.ToLookup(binding => binding.Path, Paths);
        foreach (var group in bindings.GroupBy(binding => binding.Program))
        {
            if (_lookup is not null && group.Any(binding => binding.Instance is not null &&
                candidates[binding.Path].Any(worker => !_instances.ContainsKey(worker.Identity))))
            {
                result[group.Key] = CheckingInstance;
                continue;
            }
            var matched = group.Select(binding => Match(binding, byPath[binding.Path], candidates[binding.Path], scan.UnreadableNames)).ToArray();
            if (matched.Any(worker => worker is null)) continue;
            var workers = matched.OfType<WorkerSnapshot>().ToArray();
            double? usage = workers.All(worker => cpu.ContainsKey(worker.Identity)) ? workers.Sum(worker => cpu[worker.Identity]) : null;
            double? memory = workers.All(worker => worker.WorkingSet is not null) ? workers.Sum(worker => worker.WorkingSet!.Value) / 1048576d : null;
            result[group.Key] = new(usage, memory, $"已确认 {workers.Length} 个所选模拟器进程。工作集为这些进程的合计。");
        }
        return result;
    }

    private WorkerSnapshot? Match(Binding binding, IEnumerable<Binding> owners, IEnumerable<WorkerSnapshot> candidates,
        IReadOnlySet<string> unreadable)
    {
        if (unreadable.Contains(Path.GetFileName(binding.Path)) || owners.Count(owner => owner.Instance == binding.Instance) != 1)
            return null;
        var current = candidates.ToArray();
        if (binding.Instance is null)
            return owners.Count() == 1 && current.Length == 1 ? current[0] : null;
        if (current.Any(worker => !_instances.TryGetValue(worker.Identity, out var index) || index is null)) return null;
        var matching = current.Where(worker => _instances[worker.Identity] == binding.Instance).Take(2).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private void UpdateInstanceNumbers(HashSet<ProcessIdentity> current, long stamp, CancellationToken cancellation)
    {
        foreach (var identity in _instances.Keys.Where(key => !current.Contains(key)).ToArray()) _instances.Remove(identity);
        if (_lookup is { IsCompleted: true } completed)
        {
            if (completed.IsCompletedSuccessfully)
            {
                foreach (var (identity, index) in completed.Result)
                    if (current.Contains(identity)) _instances[identity] = index;
            }
            else _ = completed.Exception;
            _lookup = null;
        }
        if (_lookup is not null || current.Count == 0) return;
        var pending = current.Where(identity => !_instances.ContainsKey(identity) ||
            (_instances[identity] is null && _lookupAt is { } prior && clock.GetElapsedTime(prior, stamp) >= TimeSpan.FromSeconds(15)))
            .Take(24).ToArray();
        if (pending.Length == 0) return;
        _lookupAt = stamp;
        _lookup = Task.Run(() => pending.ToDictionary(identity => identity, identity =>
        {
            cancellation.ThrowIfCancellationRequested();
            return ParseInstance(platform.ReadEmulatorArguments(identity, cancellation));
        }), cancellation);
    }

    private static int? ParseInstance(string[]? arguments)
    {
        if (arguments is null) return null;
        int? match = null;
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            var split = argument.IndexOf('=');
            var option = split < 0 ? argument : argument[..split];
            if (!InstanceOptions.Contains(option, Paths)) continue;
            var value = split >= 0 ? argument[(split + 1)..] : ++index < arguments.Length ? arguments[index] : "";
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                (match is { } prior && prior != number)) return null;
            match = number;
        }
        return match;
    }

    private static bool Valid(ProgramInstallation program)
    {
        try { InstallationCatalog.ValidateEmulatorMonitoring(program.EmulatorMonitor!); return true; }
        catch (ArgumentException) { return false; }
    }

    private static IEnumerable<string> Images(EmulatorMonitoring setup)
    {
        yield return setup.ImagePath;
        if (setup.WorkerImagePath is { } worker) yield return worker;
    }

    private sealed record Binding(Guid Program, string Path, int? Instance);
}
