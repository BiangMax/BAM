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

    public ResourceSample Sample(IReadOnlyList<ProgramInstallation> programs, bool readResources,
        long stamp, DateTimeOffset observedAt, CancellationToken cancellation)
    {
        var configured = programs.Where(program => program.EmulatorMonitor is not null).ToArray();
        var result = new ResourceSample { Unconfirmed = readResources ? configured.Length : 0 };
        foreach (var program in configured)
            result.Usage[program.Id] = readResources ? Unavailable : new(null, null, "资源采样已暂停。");
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
                result.Usage[group.Key] = CheckingInstance;
                continue;
            }
            var matched = group.Select(binding => Match(binding, byPath[binding.Path], candidates[binding.Path], scan.UnreadableNames, _instances)).ToArray();
            if (matched.FirstOrDefault(match => match.Issue is not null).Issue is { } issue)
            {
                if (matched.All(match => match.Absent)) result.Unconfirmed--;
                result.Usage[group.Key] = new(null, null, issue);
                continue;
            }
            var workers = matched.Select(match => match.Worker!).ToArray();
            result.Unconfirmed--;
            foreach (var worker in workers)
                result.Processes[worker.Identity] = (cpu.TryGetValue(worker.Identity, out var value) ? value : null,
                    worker.PrivateWorkingSet / 1048576d);
            double? usage = workers.All(worker => cpu.ContainsKey(worker.Identity)) ? workers.Sum(worker => cpu[worker.Identity]) : null;
            double? memory = workers.All(worker => worker.PrivateWorkingSet is not null) ? workers.Sum(worker => worker.PrivateWorkingSet!.Value) / 1048576d : null;
            result.Usage[group.Key] = new(usage, memory, $"已确认 {workers.Length} 个所选模拟器进程。内存占用为这些进程的私有工作集合计。")
            {
                SampledAt = usage is not null || memory is not null ? observedAt : null
            };
        }
        return result;
    }

    public (EmulatorCloseTarget? Target, string? Issue) ResolveCloseTarget(ProgramInstallation program,
        IReadOnlyList<ProgramInstallation> programs, CancellationToken cancellation)
    {
        if (program.EmulatorMonitor is not { } setup || !Valid(program))
            return (null, "请先设置模拟器实例的监控路径。");
        var configuredPaths = Images(setup).ToHashSet(Paths);
        // The MuMu Nx launcher can outlive the Android device and serve multiple devices.
        if (Paths.Equals(Path.GetFileName(setup.ImagePath), "MuMuNxMain.exe"))
        {
            if (setup.WorkerImagePath is not { } device || !Paths.Equals(Path.GetFileName(device), "MuMuNxDevice.exe"))
                return (null, "请将 MuMu Nx 安卓设备进程加入模拟器监控。");
            setup = new(device, setup.InstanceNumber);
        }
        else if (Paths.Equals(Path.GetFileName(setup.ImagePath), "MuMuNxDevice.exe") &&
                 Paths.Equals(Path.GetFileName(setup.WorkerImagePath), "MuMuNxMain.exe"))
            setup = setup with { WorkerImagePath = null };

        var paths = Images(setup).ToHashSet(Paths);
        if (programs.Any(other => other.WorkerPaths.Any(paths.Contains) ||
            (other.Id != program.Id && SharesEmulator(other, program.Emulator?.Entry, setup, configuredPaths))))
            return (null, "配置中的模拟器可能由其他软件使用，无法确认独占的关闭目标。");

        var scan = platform.ScanEmulatorWindows(paths, cancellation);
        if (!scan.Complete) return (null, "未能完成模拟器进程枚举。");
        var candidates = scan.Workers.ToLookup(worker => worker.Identity.ImagePath, Paths);
        Dictionary<ProcessIdentity, int?> instances = [];
        if (setup.InstanceNumber is not null)
        {
            var identities = scan.Workers.Select(worker => worker.Identity).ToArray();
            var arguments = platform.ReadEmulatorArgumentsBatch(identities, cancellation);
            foreach (var identity in identities) instances[identity] = ParseInstance(arguments.GetValueOrDefault(identity));
        }

        var primary = MatchPath(setup.ImagePath);
        if (primary.Worker is null) return (null, primary.Issue);
        WorkerSnapshot? companion = null;
        if (setup.WorkerImagePath is { } companionPath)
        {
            var match = MatchPath(companionPath);
            if (match.Worker is null) return (null, match.Issue);
            companion = match.Worker;
        }

        var windowWorker = primary.Worker;
        if (windowWorker.Window is not { Responding: true } window)
            return (null, "未找到唯一且有响应的模拟器实例窗口。请打开实例窗口后重试。");
        if (CommandPolicy.GetWindowControlRestriction(windowWorker.WindowControlAllowed) is { } issue)
            return (null, $"模拟器：{issue}");
        return (new(windowWorker.Identity, window, companion?.Identity), null);

        (WorkerSnapshot? Worker, string? Issue, bool Absent) MatchPath(string path)
        {
            var binding = new Binding(program.Id, path, setup.InstanceNumber);
            return Match(binding, [binding], candidates[path], scan.UnreadableNames, instances);
        }
    }

    private static bool SharesEmulator(ProgramInstallation other, LaunchEntry? launcher, EmulatorMonitoring setup, HashSet<string> paths)
    {
        if (other.EmulatorMonitor is { } monitor && Images(monitor).Any(paths.Contains) &&
            (setup.InstanceNumber is null || monitor.InstanceNumber is null || setup.InstanceNumber == monitor.InstanceNumber))
            return true;
        if (other.Emulator?.Entry is not { } entry) return false;
        var target = entry.ResolvedTarget ?? entry.TargetPath;
        return (other.EmulatorMonitor is null && paths.Contains(target)) ||
            (launcher is not null && Paths.Equals(launcher.ResolvedTarget ?? launcher.TargetPath, target) && launcher.Arguments == entry.Arguments);
    }

    private static (WorkerSnapshot? Worker, string? Issue, bool Absent) Match(Binding binding, IEnumerable<Binding> owners,
        IEnumerable<WorkerSnapshot> candidates, IReadOnlySet<string> unreadable, IReadOnlyDictionary<ProcessIdentity, int?> instances)
    {
        if (unreadable.Contains(Path.GetFileName(binding.Path)))
            return (null, "存在无法读取的同名进程，暂无法确认模拟器实例。", false);
        if (owners.Count(owner => owner.Instance == binding.Instance) != 1)
            return (null, "多个脚本选择了相同的模拟器进程路径和实例编号。", false);
        var current = candidates.ToArray();
        if (current.Length == 0) return (null, "未找到配置中的模拟器进程，请核对所选 EXE。", true);
        if (binding.Instance is null)
            return owners.Count() == 1 && current.Length == 1 ? (current[0], null, false) :
                (null, "所选 EXE 对应多个进程或脚本，请填写对应实例编号。", false);
        if (current.Any(worker => !instances.TryGetValue(worker.Identity, out var index) || index is null))
            return (null, "无法读取或识别同路径候选进程的实例编号。", false);
        var matching = current.Where(worker => instances[worker.Identity] == binding.Instance).Take(2).ToArray();
        return matching.Length switch
        {
            0 => (null, "所选进程中没有与配置编号匹配的模拟器实例。", true),
            1 => (matching[0], null, false),
            _ => (null, "同一路径下有多个进程报告相同的模拟器实例编号。", false)
        };
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
        _lookup = Task.Run(() =>
        {
            var arguments = platform.ReadEmulatorArgumentsBatch(pending, cancellation);
            return pending.ToDictionary(identity => identity, identity => ParseInstance(arguments.GetValueOrDefault(identity)));
        }, cancellation);
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

    internal sealed class ResourceSample
    {
        public Dictionary<Guid, EmulatorResourceUsage> Usage { get; } = [];
        public Dictionary<ProcessIdentity, (double? Cpu, double? MemoryMiB)> Processes { get; } = [];
        public int Unconfirmed { get; set; }
    }
}
