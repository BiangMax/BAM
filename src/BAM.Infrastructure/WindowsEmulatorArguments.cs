using System.Runtime.InteropServices;
using System.Text;
using BAM.Core;
using Microsoft.CSharp.RuntimeBinder;

namespace BAM.Infrastructure;

public sealed partial class WindowsProgramPlatform
{
    public string[]? ReadEmulatorArguments(ProcessIdentity identity, CancellationToken cancellation) =>
        ReadEmulatorArgumentsBatch([identity], cancellation).GetValueOrDefault(identity);

    public IReadOnlyDictionary<ProcessIdentity, string[]?> ReadEmulatorArgumentsBatch(IReadOnlyList<ProcessIdentity> identities,
        CancellationToken cancellation)
    {
        var result = new Dictionary<ProcessIdentity, string[]?>();
        object? locator = null;
        object? connection = null;
        try
        {
            var buffer = new StringBuilder(1024);
            foreach (var identity in identities)
            {
                cancellation.ThrowIfCancellationRequested();
                using var handle = OpenProcess(Query | Synchronize, false, identity.Pid);
                if (handle.IsInvalid || !IsLive(handle) || !identity.Equals(ReadIdentity(handle, identity.Pid, buffer, out _)))
                    continue;

                object? process = null;
                try
                {
                    // Reuse the read-only WMI session across the candidates in this sample.
                    if (connection is null)
                    {
                        if (Type.GetTypeFromProgID("WbemScripting.SWbemLocator") is not { } type) return result;
                        locator ??= Activator.CreateInstance(type);
                        connection = ((dynamic)locator!).ConnectServer(".", @"root\cimv2", "", "", "", "", 128);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    process = ((dynamic)connection!).Get($"Win32_Process.Handle=\"{identity.Pid}\"", 0);
                    var commandLine = ReadCommandLineProperty(process!);
                    cancellation.ThrowIfCancellationRequested();
                    if (IsLive(handle) && commandLine is { Length: > 0 and <= 32767 })
                        result[identity] = SplitCommandLine(commandLine);
                }
                catch (Exception error) when (error is COMException or UnauthorizedAccessException or RuntimeBinderException)
                {
                    // Keep this candidate unknown and continue checking the other verified processes.
                }
                finally { if (process is not null) Marshal.FinalReleaseComObject(process); }
            }
            return result;
        }
        finally
        {
            if (connection is not null) Marshal.FinalReleaseComObject(connection);
            if (locator is not null) Marshal.FinalReleaseComObject(locator);
        }
    }

    private static string? ReadCommandLineProperty(object process)
    {
        object? properties = null;
        object? property = null;
        try
        {
            // The direct dynamic CommandLine member can fail on subsequent Win32_Process objects.
            properties = ((dynamic)process).Properties_;
            property = ((dynamic)properties!).Item("CommandLine", 0);
            return ((dynamic)property!).Value as string;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            if (properties is not null) Marshal.FinalReleaseComObject(properties);
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
