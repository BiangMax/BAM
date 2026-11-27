using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BAM.Core;
using Microsoft.Win32.SafeHandles;

namespace BAM.Infrastructure;

public sealed partial class WindowsProgramPlatform : IProgramPlatform, IDisposable
{
    private const uint Query = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint Terminate = 1;
    private const uint WaitTimeout = 258;
    private readonly Dictionary<ProcessIdentity, SafeProcessHandle> _watched = [];
    private readonly int? _integrity;

    public WindowsProgramPlatform()
    {
        using var process = OpenProcess(Query, false, Environment.ProcessId);
        _integrity = ReadIntegrity(process);
    }

    public static bool? IsCurrentProcessElevated()
    {
        using var process = OpenProcess(Query, false, Environment.ProcessId);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return null;
        using (token)
            return GetTokenElevation(token, 20, out var elevated, sizeof(int), out _) ? elevated != 0 : null;
    }

    // Scans are serialized by the manager. References are retained only for matched workers.
    public ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, CancellationToken cancellation) =>
        Scan(paths, readResources, readWindows: true, watchScripts: true, cancellation);

    public ProcessScan ScanEmulators(IReadOnlySet<string> paths, CancellationToken cancellation) =>
        Scan(paths, readResources: true, readWindows: false, watchScripts: false, cancellation);

    public ProcessScan ScanEmulatorWindows(IReadOnlySet<string> paths, CancellationToken cancellation) =>
        Scan(paths, readResources: false, readWindows: true, watchScripts: false, cancellation);

    public bool HasProcessExited(ProcessIdentity identity)
    {
        using var handle = OpenProcess(Query | Synchronize, false, identity.Pid);
        if (handle.IsInvalid) return Marshal.GetLastPInvokeError() == 87;
        return WaitForSingleObject(handle, 0) == 0 ||
            (GetProcessTimes(handle, out var creation, out _, out _, out _) && creation != identity.CreatedAtUtc.ToFileTime());
    }

    private ProcessScan Scan(IReadOnlySet<string> paths, bool readResources, bool readWindows, bool watchScripts,
        CancellationToken cancellation)
    {
        var names = paths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
        List<WorkerSnapshot> found = [];
        Dictionary<ProcessIdentity, uint?> exits = [];
        var seen = new HashSet<ProcessIdentity>();
        var complete = true;
        if (paths.Count > 0)
        {
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            complete = !snapshot.IsInvalid && Process32First(snapshot, ref entry);
            if (complete)
            {
                var buffer = new StringBuilder(1024);
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.Pid == 0 || entry.Pid == Environment.ProcessId ||
                        entry.ImageName is not { } name || !names.Contains(name)) continue;
                    var handle = OpenProcess(Query | Synchronize, false, entry.Pid);
                    try
                    {
                        if (handle.IsInvalid)
                        {
                            if (Marshal.GetLastPInvokeError() != 87) unreadable.Add(name);
                            continue;
                        }
                        var identity = ReadIdentity(handle, entry.Pid, buffer, out var cpu);
                        if (identity is null)
                        {
                            if (IsLive(handle)) unreadable.Add(name);
                            continue;
                        }
                        if (!paths.Contains(identity.ImagePath))
                        {
                            if (paths.Contains(WindowsImagePath.Resolve(identity.ImagePath))) unreadable.Add(name);
                            continue;
                        }
                        if (!IsLive(handle)) continue;
                        long? memory = readResources ? ReadPrivateWorkingSet(handle) : null;
                        var controlAllowed = readWindows ? CanControlWindow(handle) : null;
                        if (!watchScripts)
                        {
                            if (IsLive(handle)) found.Add(new(identity, readResources ? cpu : null, memory, null, controlAllowed, false));
                            continue;
                        }
                        using var terminate = OpenProcess(Terminate | Query | Synchronize, false, identity.Pid);
                        var canTerminate = !terminate.IsInvalid && IsLive(handle) &&
                            identity.Equals(ReadIdentity(terminate, identity.Pid, buffer, out _));
                        if (!IsLive(handle)) continue;
                        found.Add(new(identity, readResources ? cpu : null, memory, null, controlAllowed, canTerminate));
                        seen.Add(identity);
                        if (_watched.TryAdd(identity, handle)) handle = null!;
                    }
                    finally { handle?.Dispose(); }
                } while (Process32Next(snapshot, ref entry));
                complete = Marshal.GetLastPInvokeError() == 18;
            }
        }

        if (readWindows && found.Count > 0)
        {
            var pids = found.Select(worker => worker.Identity.Pid).ToHashSet();
            var windows = new Dictionary<int, ProgramWindow>();
            var ambiguous = watchScripts ? null : new HashSet<int>();
            var enumerated = EnumWindows((window, _) => AddWindow(window, pids, windows, ambiguous), 0);
            if (!watchScripts && !enumerated) return new(false, unreadable, found, exits);
            for (var index = 0; index < found.Count; index++)
                if (ambiguous?.Contains(found[index].Identity.Pid) != true &&
                    windows.TryGetValue(found[index].Identity.Pid, out var window))
                    found[index] = found[index] with { Window = window };
        }

        if (!watchScripts) return new(complete, unreadable, found, exits);

        foreach (var (identity, handle) in _watched)
        {
            if (WaitForSingleObject(handle, 0) == 0)
            {
                exits.Add(identity, GetExitCodeProcess(handle, out var code) ? code : null);
                handle.Dispose();
                _watched.Remove(identity);
            }
            else if (!paths.Contains(identity.ImagePath))
            {
                handle.Dispose();
                _watched.Remove(identity);
            }
            else if (!seen.Contains(identity))
                unreadable.Add(Path.GetFileName(identity.ImagePath));
        }
        if (exits.Count > 0) found.RemoveAll(worker => exits.ContainsKey(worker.Identity));
        return new(complete, unreadable, found, exits);
    }

    public CommandResult Launch(LaunchEntry entry)
    {
        try
        {
            if (entry.IsBatch && !entry.HasSafeBatchArguments)
                return new(CommandStatus.Denied, "启动脚本参数包含不支持的字符。");
            if (!entry.IsBatch && !Path.GetExtension(entry.TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return new(CommandStatus.Unsupported, "不支持该启动入口。");

            var args = entry.Arguments;
            var file = entry.TargetPath;
            if (entry.IsBatch)
            {
                file = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var suffix = string.IsNullOrWhiteSpace(args) ? "" : " " + args.Trim();
                args = $"/d /s /v:off /c \"\"{entry.TargetPath}\"{suffix}\"";
            }

            var info = new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                WorkingDirectory = entry.WorkingDirectory
            };
            if (entry.RunAs) info.Verb = "runas";
            using var process = Process.Start(info);
            return new(CommandStatus.Accepted, "已提交启动请求，等待确认工作进程。");
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 5 or 193 or 267 or 740 or 1223)
        {
            return NativeError(error.NativeErrorCode, "启动");
        }
        catch (Win32Exception) { return new(CommandStatus.Unknown, "Windows 未返回明确的启动结果。"); }
        catch (InvalidOperationException) { return new(CommandStatus.Unknown, "Windows 未返回明确的启动结果。"); }
    }

    public CommandResult Control(ScriptDefinition script, ProgramAction action, ProcessIdentity identity, ProgramWindow? window)
    {
        var capability = action switch
        {
            ProgramAction.Activate => CapabilityKind.ActivateWindow,
            ProgramAction.Close => CapabilityKind.CloseProcess,
            ProgramAction.Force => CapabilityKind.ForceTerminate,
            _ => (CapabilityKind?)null
        };
        if (capability is not { } kind) return new(CommandStatus.Unsupported);
        if (identity.Pid == Environment.ProcessId) return new(CommandStatus.Denied, "无法管理 BAM 自身。");

        using var handle = OpenProcess(Query | Synchronize | (action == ProgramAction.Force ? Terminate : 0), false, identity.Pid);
        if (handle.IsInvalid) return NativeError(Marshal.GetLastPInvokeError(), "访问进程");
        var current = ReadIdentity(handle, identity.Pid, new StringBuilder(32768), out _);
        if (current is null) return new(CommandStatus.Denied, "无法重新读取进程身份。");
        var check = CommandPolicy.CheckTarget(script, kind, new(kind, CapabilityStatus.Available),
            identity, ProcessInspection.FromCandidate(current));
        if (check.Status != CommandStatus.Accepted) return check;
        if (!IsLive(handle)) return new(CommandStatus.IdentityChanged, "原实例已退出。");
        if (action == ProgramAction.Force)
            return TerminateProcess(handle, 1) ? new(CommandStatus.Accepted, "已请求结束该进程。")
                : NativeError(Marshal.GetLastPInvokeError(), "结束进程");

        if (window is null || !OwnsWindow(window.Handle, identity.Pid))
            return new(CommandStatus.IdentityChanged, "原窗口已改变。");
        if (CommandPolicy.GetWindowControlRestriction(CanControlWindow(handle)) is { } restriction)
            return new(CommandStatus.Denied, restriction);
        if (!IsLive(handle) || !OwnsWindow(window.Handle, identity.Pid))
            return new(CommandStatus.IdentityChanged, "原窗口或进程已改变。");
        if (action == ProgramAction.Close)
        {
            if (IsHungAppWindow(window.Handle)) return new(CommandStatus.Denied, "目标窗口无响应，无法正常关闭。");
            return PostMessage(window.Handle, 0x0010, 0, 0)
                ? new(CommandStatus.Accepted, "已向原窗口提交关闭请求。")
                : NativeError(Marshal.GetLastPInvokeError(), "提交关闭请求");
        }

        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
        if (!IsLive(handle) || !OwnsWindow(window.Handle, identity.Pid))
            return new(CommandStatus.IdentityChanged, "原窗口或进程已改变。");
        if (GetForegroundWindow() == window.Handle || SetForegroundWindow(window.Handle))
            return new(CommandStatus.Completed, "Windows 已接受前台激活。");
        return new(CommandStatus.Denied, "Windows 未允许前台切换，请使用任务栏打开窗口。");
    }

    private bool? CanControlWindow(SafeProcessHandle process) =>
        _integrity is { } current && ReadIntegrity(process) is { } target ? target <= current : null;

    private static CommandResult NativeError(int code, string action) => code switch
    {
        5 or 740 => new(CommandStatus.Denied, $"权限不足，无法{action}（Windows {code}）。"),
        1223 => new(CommandStatus.Denied, "用户取消了 Windows 权限请求。"),
        87 or 1168 => new(CommandStatus.IdentityChanged, "目标已改变或退出。"),
        _ => new(CommandStatus.Failed, $"{action}失败（Windows {code}）。")
    };

    private static bool AddWindow(nint handle, HashSet<int> pids, Dictionary<int, ProgramWindow> windows, HashSet<int>? ambiguous)
    {
        if (GetWindowThreadProcessId(handle, out var pid) == 0 || !pids.Contains(pid) ||
            (ambiguous is null && windows.ContainsKey(pid))) return true;
        if (!IsWindowVisible(handle) || GetWindow(handle, 4) != 0) return true;
        var title = new StringBuilder(513);
        if (GetWindowText(handle, title, title.Capacity) == 0) return true;
        if (!windows.TryAdd(pid, new(handle, title.ToString(), IsIconic(handle) ? WindowState.Minimized : WindowState.Visible,
            !IsHungAppWindow(handle)))) ambiguous?.Add(pid);
        return true;
    }

    private static bool OwnsWindow(nint window, int pid) => IsWindowVisible(window) &&
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid;

    private static bool IsLive(SafeProcessHandle process) => WaitForSingleObject(process, 0) == WaitTimeout;

    private static ProcessIdentity? ReadIdentity(SafeProcessHandle handle, int pid, StringBuilder buffer, out TimeSpan? cpu)
    {
        var size = (uint)buffer.Capacity;
        buffer.Clear();
        cpu = null;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
        {
            if (Marshal.GetLastPInvokeError() != 122 || buffer.Capacity >= 32768) return null;
            buffer.EnsureCapacity(32768);
            size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
        }
        if (!GetProcessTimes(handle, out var created, out _, out var kernel, out var user)) return null;
        cpu = TimeSpan.FromTicks(kernel + user);
        try { return new(WindowsImagePath.ExpandShortNames(buffer.ToString()), pid, DateTimeOffset.FromFileTime(created)); }
        catch (ArgumentException) { return null; }
    }

    private static long? ReadPrivateWorkingSet(SafeProcessHandle handle)
    {
        var counters = new MemoryCountersEx2 { Size = (uint)Marshal.SizeOf<MemoryCountersEx2>(), PrivateWorkingSet = nuint.MaxValue };
        return GetProcessMemoryInfo(handle, ref counters, counters.Size) && counters.PrivateWorkingSet != nuint.MaxValue
            ? (long)counters.PrivateWorkingSet : null;
    }

    private static int? ReadIntegrity(SafeProcessHandle process)
    {
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, 25, 0, 0, out var size);
            if (size is <= 0 or > 4096) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public void Dispose()
    {
        foreach (var handle in _watched.Values) handle.Dispose();
        _watched.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage;
        public int Pid;
        public nuint Heap;
        public uint Module, ThreadCount;
        public int ParentPid, Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImageName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCountersEx2
    {
        public uint Size, Faults;
        public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile;
        public nuint PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommitUsage;
    }

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCountersEx2 counters, uint size);
    [DllImport("advapi32.dll")]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, nint buffer, int size, out int required);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenElevation(SafeAccessTokenHandle token, int type, out int elevated, int size, out int required);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}
