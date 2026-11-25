using System.Text.Json;
using BAM.Core;

namespace BAM.Adapters;

public sealed class InstallationCatalog(IProgramFiles files)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] OtherPrograms =
        ["adb.exe", "fastboot.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "explorer.exe", "BAM.exe"];
    private static readonly string[] BbWorkerLocations =
        [@"dist\BBchannel64\BBchannel.exe", @"dist\BBchannel\BBchannel.exe"];

    public IReadOnlyList<ProgramInstallation> Discover(CancellationToken cancellation)
    {
        var discovered = new List<ProgramInstallation>();
        foreach (var path in files.FindShortcuts(cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var entry = files.ReadEntry(path);
                var kind = Identify(entry);
                if (kind == ProgramKind.Generic) continue;
                var program = Describe(Guid.NewGuid(), KindName(kind), entry, kind, null);
                Validate(program, requireFiles: true);
                if (!discovered.Any(existing => Overlaps(existing, program))) discovered.Add(program);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A broken link has no confirmed installation. Existing records are retained separately.
            }
        }
        return discovered.OrderBy(program => program.Kind).ToArray();
    }

    public ProgramInstallation Import(string path)
    {
        var entry = files.ReadEntry(path);
        var kind = Identify(entry);
        return Describe(Guid.NewGuid(), kind == ProgramKind.Generic
            ? Path.GetFileNameWithoutExtension(entry.TargetPath) : KindName(kind), entry, kind, null);
    }

    public ProgramInstallation Edit(ProgramInstallation source, string name, string directory, string arguments,
        string? explicitWorker)
    {
        var entry = source.Entry with { WorkingDirectory = directory.Trim(), Arguments = arguments };
        var edited = Describe(source.Id, name.Trim(), entry, source.Kind,
            string.IsNullOrWhiteSpace(explicitWorker) ? null : explicitWorker.Trim());
        Validate(edited, requireFiles: true);
        return edited;
    }

    public void Validate(ProgramInstallation program, bool requireFiles)
    {
        if (program.Id == Guid.Empty || string.IsNullOrWhiteSpace(program.Name) || program.Name.Length > 80
            || program.Name.Any(char.IsControl) || !Enum.IsDefined(program.Kind))
            throw new ArgumentException("请填写有效的软件名称。");
        var entry = program.Entry;
        ValidatePath(entry.OriginalPath, ".lnk", ".exe", ".cmd", ".bat");
        ValidatePath(entry.TargetPath, ".exe", ".cmd", ".bat");
        if (!Paths.Equals(Path.GetExtension(entry.OriginalPath), ".lnk") && !Paths.Equals(entry.OriginalPath, entry.TargetPath))
            throw new ArgumentException("直接入口与启动目标不一致，请重新定位。");
        if (entry.ResolvedTarget is not null) ValidatePath(entry.ResolvedTarget, ".exe", ".cmd", ".bat");
        ValidatePath(entry.WorkingDirectory);
        ValidatePath(program.InstallDirectory);
        if (entry.IconPath is not null) ValidatePath(entry.IconPath);
        if (entry.Arguments is null || entry.Arguments.Length > 4096 || entry.Arguments.Any(char.IsControl))
            throw new ArgumentException("启动参数有换行、控制字符或过长。");
        if (entry.IsBatch && !entry.HasSafeBatchArguments)
            throw new ArgumentException("CMD/BAT 参数须成对使用双引号，路径及参数不可包含命令重定向或变量展开字符。");
        if (program.WorkerPaths is not { Length: > 0 and <= 2 })
            throw new ArgumentException("请选择实际工作程序 EXE。");
        foreach (var worker in program.WorkerPaths)
        {
            ValidatePath(worker, ".exe");
            if (OtherPrograms.Contains(Path.GetFileName(worker), Paths) ||
                (program.Kind != ProgramKind.Generic && !Paths.Equals(Path.GetFileName(worker), ExpectedWorkerName(program.Kind))))
                throw new ArgumentException("该文件不是此安装可管理的工作程序。");
        }
        if (requireFiles && !HasWorkerBinding(program)) throw new ArgumentException("工作程序与此安装不匹配。");
        if (requireFiles && Availability(program) is { } issue) throw new ArgumentException(issue);
    }

    public bool HasWorkerBinding(ProgramInstallation program)
    {
        if (program.Kind == ProgramKind.Generic) return true;
        var target = program.Entry.TargetPath;
        var workers = program.Kind == ProgramKind.BBchannel && program.Entry.IsBatch
            ? BbWorkerLocations.Select(location => Path.Combine(Path.GetDirectoryName(target)!, location))
            : [target];
        var allowed = workers.Select(files.ResolveImagePath).ToHashSet(Paths);
        return program.WorkerPaths.All(allowed.Contains);
    }

    public string? Availability(ProgramInstallation program)
    {
        if (!files.FileExists(program.Entry.OriginalPath)) return "启动入口已失效，请重新定位。";
        if (!files.FileExists(program.Entry.TargetPath)) return "启动目标已失效，请重新定位。";
        if (program.Kind == ProgramKind.BBchannel && !program.Entry.IsBatch &&
            BbRootForWorker(program.Entry.ResolvedTarget ?? program.Entry.TargetPath) is { } bbRoot &&
            files.FileExists(Path.Combine(bbRoot, "启动.cmd")))
            return "此 BBchannel 安装需要原启动.cmd，请重新定位。";
        if (program.Entry.ResolvedTarget is { } target && !Paths.Equals(target, files.ResolveImagePath(program.Entry.TargetPath)))
            return "启动目标的实际路径已变更，请重新定位。";
        if (!files.DirectoryExists(program.Entry.WorkingDirectory)) return "工作目录已失效，请重新定位。";
        if (!program.WorkerPaths.Any(files.FileExists)) return "工作程序已失效，请重新定位。";
        if (HasLinkChanged(program.Entry)) return "快捷方式内容已变更，请重新选择入口。";
        return null;
    }

    public ProgramMetadata Inspect(ProgramInstallation program)
    {
        if (!HasWorkerBinding(program))
            return new("版本未知", "版本未知", null, false, "工作程序与此安装不匹配，请重新定位。", WorkerBindingValid: false);
        var issue = Availability(program);
        var worker = program.WorkerPaths.FirstOrDefault(files.FileExists);
        var entry = program.Entry;
        var icon = entry.IconPath is { } path ? files.ReadIcon(path, entry.IconIndex) : null;
        icon ??= worker is null ? null : files.ReadIcon(worker, 0);
        var product = worker is null ? null : DisplayVersion(files.ReadProductVersion(worker));
        var resource = program.Kind == ProgramKind.MaaEnd ? MaaEndResource(program.InstallDirectory) : null;
        var bundle = program.Kind == ProgramKind.BBchannel ? BbPackage(program.InstallDirectory) : null;
        var version = product ?? resource ?? bundle ?? "版本未知";
        List<string> parts = [];
        if (product is not null) parts.Add($"{(program.Kind == ProgramKind.MaaEnd ? "MXU GUI" : "程序")} {product}");
        if (resource is not null) parts.Add($"资源 {resource}");
        if (bundle is not null) parts.Add($"安装清单 {bundle}");
        return new(version, parts.Count == 0 ? "版本未知" : string.Join(" · ", parts), icon, issue is null, issue,
            ProductVersion: product, ResourceVersion: resource);
    }

    public static bool Overlaps(ProgramInstallation left, ProgramInstallation right) =>
        Paths.Equals(left.Entry.OriginalPath, right.Entry.OriginalPath) ||
        left.WorkerPaths.Intersect(right.WorkerPaths, Paths).Any();

    private bool HasLinkChanged(LaunchEntry entry)
    {
        if (!Paths.Equals(Path.GetExtension(entry.OriginalPath), ".lnk")) return false;
        try
        {
            var current = files.ReadEntry(entry.OriginalPath);
            // The saved working directory and arguments can be overridden in BAM.
            return !Paths.Equals(current.TargetPath, entry.TargetPath) || current.RunAs != entry.RunAs;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private ProgramInstallation Describe(Guid id, string name, LaunchEntry entry, ProgramKind kind,
        string? explicitWorker)
    {
        var root = Path.GetDirectoryName(entry.TargetPath)!;
        if (kind == ProgramKind.BBchannel && !entry.IsBatch)
            root = BbRootForWorker(entry.ResolvedTarget ?? entry.TargetPath) ?? root;
        string[] workers = explicitWorker is not null ? [explicitWorker]
            : kind == ProgramKind.BBchannel && entry.IsBatch
                ? BbWorkerLocations.Select(location => Path.Combine(root, location)).ToArray()
                : entry.IsBatch ? [] : [entry.TargetPath];
        return new(id, name, kind, entry, root, workers.Select(files.ResolveImagePath).Distinct(Paths).ToArray());
    }

    private ProgramKind Identify(LaunchEntry entry)
    {
        var target = Path.GetFileName(entry.ResolvedTarget ?? entry.TargetPath);
        if (Paths.Equals(target, "MaaEnd.exe")) return ProgramKind.MaaEnd;
        if (Paths.Equals(target, "MAA.exe")) return ProgramKind.Maa;
        if (Paths.Equals(target, "BBchannel.exe") || (Paths.Equals(target, "启动.cmd") &&
            (Path.GetFileNameWithoutExtension(entry.OriginalPath).Contains("BBchannel", StringComparison.OrdinalIgnoreCase) ||
             BbWorkerLocations.Any(location => files.FileExists(Path.Combine(Path.GetDirectoryName(entry.TargetPath)!, location))))))
            return ProgramKind.BBchannel;
        return ProgramKind.Generic;
    }

    private static string KindName(ProgramKind kind) => kind switch
    {
        ProgramKind.MaaEnd => "MaaEnd",
        ProgramKind.Maa => "MAA",
        _ => "BBchannel"
    };

    private static string ExpectedWorkerName(ProgramKind kind) => KindName(kind) + ".exe";

    private static string? BbRootForWorker(string worker)
    {
        var directory = Path.GetDirectoryName(worker);
        var dist = directory is null ? null : Path.GetDirectoryName(directory);
        return dist is not null && Paths.Equals(Path.GetFileName(dist), "dist") &&
            (Paths.Equals(Path.GetFileName(directory), "BBchannel") || Paths.Equals(Path.GetFileName(directory), "BBchannel64"))
            ? Path.GetDirectoryName(dist) : null;
    }

    private static void ValidatePath(string path, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Any(char.IsControl)
            || !Paths.Equals(path, Path.GetFullPath(path))
            || (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(path), Paths)))
            throw new ArgumentException("请选择有效的本地完整路径与受支持的文件类型。");
    }

    private string? MaaEndResource(string directory)
    {
        if (files.ReadSmallText(Path.Combine(directory, "interface.json")) is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String ? DisplayVersion(version.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    private string? BbPackage(string directory)
    {
        var text = files.ReadSmallText(Path.Combine(directory, "updateInfo.txt"));
        foreach (var line in (text ?? "").Split('\n'))
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim() == "this_version")
                return DisplayVersion(pair[1]);
        }
        return null;
    }

    private static string? DisplayVersion(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 80 } && value.Any(char.IsAsciiDigit) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_' or ' ') ? value : null;
    }
}
