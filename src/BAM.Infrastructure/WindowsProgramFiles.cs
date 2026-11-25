using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using BAM.Core;

namespace BAM.Infrastructure;

public sealed class WindowsProgramFiles : IProgramFiles
{
    private readonly Dictionary<(string Path, int Index), (long Stamp, byte[]? Icon)> _icons = new();

    public IEnumerable<string> FindShortcuts(CancellationToken cancellation)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folders = new List<(string Path, int Depth)>
        {
            (desktop, 0), (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), 0)
        };
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path && Path.IsPathFullyQualified(path))
                folders.Add((Path.Combine(path, "Desktop"), 0));
        }
        folders.Add((Environment.GetFolderPath(Environment.SpecialFolder.Programs), 2));
        folders.Add((Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), 2));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, depth) in folders)
        {
            if (path.Length == 0 || !visited.Add(path)) continue;
            foreach (var shortcut in FindLinks(path, depth, cancellation)) yield return shortcut;
        }
    }

    private static IEnumerable<string> FindLinks(string directory, int depth, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // The root can be redirected. Decline traversal through secondary directory junctions.
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        string[] links;
        string[] children;
        try
        {
            links = Directory.GetFiles(directory, "*.lnk", options);
            children = depth > 0 ? Directory.GetDirectories(directory, "*", options) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in links.Order(StringComparer.OrdinalIgnoreCase)) yield return file;
        foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            foreach (var file in FindLinks(child, depth - 1, cancellation)) yield return file;
    }

    public LaunchEntry ReadEntry(string path)
    {
        var original = LocalPath(path);
        var extension = Path.GetExtension(original).ToLowerInvariant();
        if (!File.Exists(original)) throw new FileNotFoundException("找不到所选启动入口。");
        if (extension is ".exe" or ".cmd" or ".bat")
            return new(original, original, Path.GetDirectoryName(original)!, "", ResolvedTarget: ResolveImagePath(original));
        if (extension != ".lnk") throw new ArgumentException("请选择 EXE、LNK、CMD 或 BAT。");

        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("系统快捷方式读取器不可用。");
            shell = Activator.CreateInstance(type)!;
            shortcut = ((dynamic)shell).CreateShortcut(original);
            dynamic link = shortcut!;
            var target = LocalPath(Environment.ExpandEnvironmentVariables((string)link.TargetPath));
            string workingDirectory = link.WorkingDirectory;
            string arguments = link.Arguments;
            string iconLocation = link.IconLocation;
            var (iconPath, iconIndex) = ReadIconLocation(iconLocation);
            return new(original, target, string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(target)! : LocalPath(Environment.ExpandEnvironmentVariables(workingDirectory)),
                arguments, iconPath, iconIndex, RunAsFlag(original), ResolveImagePath(target));
        }
        catch (COMException error) { throw new IOException("无法读取 Windows 快捷方式。", error); }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static (string? Path, int Index) ReadIconLocation(string value)
    {
        var separator = value.LastIndexOf(',');
        var index = 0;
        var path = separator >= 0 && int.TryParse(value[(separator + 1)..], CultureInfo.InvariantCulture, out index)
            ? value[..separator] : value;
        try { return (LocalPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))), index); }
        catch (ArgumentException) { return (null, 0); }
    }

    private static bool RunAsFlag(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4C)
            throw new IOException("快捷方式头部无效。");
        return (BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) & 0x2000) != 0;
    }

    private static string LocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
            throw new ArgumentException("需要本地完整路径。");
        var path = Path.GetFullPath(value);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("需要本地完整路径。");
        return path;
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ResolveImagePath(string path) => WindowsImagePath.Resolve(path);

    public string? ReadSmallText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2_097_152) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            using var reader = new StreamReader(new MemoryStream(bytes));
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? ReadProductVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return VersionValue(version.ProductVersion) ?? VersionValue(version.FileVersion);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? VersionValue(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Trim() is "0.0.0.0" or "0.0.0" ? null : version;

    public byte[]? ReadIcon(string path, int index)
    {
        if (!File.Exists(path)) return null;
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        lock (_icons)
        {
            if (_icons.TryGetValue((path, index), out var cached) && cached.Stamp == stamp) return cached.Icon;
            if (_icons.Count >= 128) _icons.Clear();
            var icon = ExtractIcon(path, index);
            _icons[(path, index)] = (stamp, icon);
            return icon;
        }
    }

    private static byte[]? ExtractIcon(string path, int index)
    {
        if (PrivateExtractIcons(path, index, 64, 64, out var handle, out _, 1, 0) != 1 || handle == 0)
        {
            if (handle != 0) DestroyIcon(handle);
            return null;
        }
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception error) when (error is ArgumentException or ExternalException) { return null; }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height,
        out nint icon, out uint iconId, uint count, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
