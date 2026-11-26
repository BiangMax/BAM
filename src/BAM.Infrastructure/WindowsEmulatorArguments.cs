using System.Runtime.InteropServices;
using System.Text;
using BAM.Core;
using Microsoft.CSharp.RuntimeBinder;

namespace BAM.Infrastructure;

public sealed partial class WindowsProgramPlatform
{
    public string[]? ReadEmulatorArguments(ProcessIdentity identity, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var handle = OpenProcess(Query | Synchronize, false, identity.Pid);
        if (handle.IsInvalid || !IsLive(handle) ||
            !identity.Equals(ReadIdentity(handle, identity.Pid, new StringBuilder(1024), out _))) return null;

        object? locator = null;
        object? connection = null;
        object? process = null;
        try
        {
            // WMI provides the OS process property without opening the target for memory access.
            if (Type.GetTypeFromProgID("WbemScripting.SWbemLocator") is not { } type) return null;
            locator = Activator.CreateInstance(type);
            connection = ((dynamic)locator!).ConnectServer(".", @"root\cimv2", "", "", "", "", 128);
            cancellation.ThrowIfCancellationRequested();
            process = ((dynamic)connection!).Get($"Win32_Process.Handle=\"{identity.Pid}\"", 0);
            string? commandLine = ((dynamic)process!).CommandLine;
            cancellation.ThrowIfCancellationRequested();
            return IsLive(handle) && commandLine is { Length: > 0 and <= 32767 } ? SplitCommandLine(commandLine) : null;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            if (process is not null) Marshal.FinalReleaseComObject(process);
            if (connection is not null) Marshal.FinalReleaseComObject(connection);
            if (locator is not null) Marshal.FinalReleaseComObject(locator);
        }
    }

    private static string[]? SplitCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "";
            return arguments;
        }
        finally { _ = LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
