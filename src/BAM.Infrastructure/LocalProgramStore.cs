using System.Text;
using System.Text.Json;
using BAM.Core;

namespace BAM.Infrastructure;

public sealed class LocalProgramStore(string directory, bool sessionOnly = false) : IProgramStore
{
    private const int Schema = 1;
    private const int MaxBytes = 1024 * 1024;
    private readonly string _file = Path.Combine(directory, "installations.json");
    private bool _canWrite = true;
    private bool _keepBackup;

    public async Task<ProgramStoreLoad> LoadAsync(CancellationToken cancellation)
    {
        try
        {
            var data = await ReadAsync(_file, cancellation).ConfigureAwait(false);
            return new(data.Programs!, data.SuppressedPaths ?? [], null);
        }
        catch (NotSupportedException)
        {
            _canWrite = false;
            return new([], [], "安装配置由较新版本创建，保存已停用。", Readable: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (!IsMissingOrDamaged(error)) _canWrite = false;
            _keepBackup = true;
            try
            {
                var data = await ReadAsync(_file + ".bak", cancellation).ConfigureAwait(false);
                return new(data.Programs!, data.SuppressedPaths ?? [], _canWrite ? "已从上一份安装配置备份恢复。"
                    : "原配置暂不可读，已读取备份并停用保存。");
            }
            catch (NotSupportedException)
            {
                _canWrite = false;
                return new([], [], "安装配置备份由较新版本创建，保存已停用。", Readable: false);
            }
            catch (Exception backup) when (backup is IOException or UnauthorizedAccessException or JsonException)
            {
                if (!_canWrite || !IsMissingOrDamaged(backup))
                {
                    _canWrite = false;
                    return new([], [], "安装配置暂不可读，已停用保存。请检查文件权限或占用后重新打开 BAM。", Readable: false);
                }
                if ((error is FileNotFoundException or DirectoryNotFoundException) &&
                    (backup is FileNotFoundException or DirectoryNotFoundException)) return new([], [], null);
                return new([], [], "无法读取安装配置及备份，原文件将在下次保存时保留为损坏副本。", Readable: false);
            }
        }
    }

    private static bool IsMissingOrDamaged(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException or JsonException;

    public async Task SaveAsync(IReadOnlyList<ProgramInstallation> programs, IReadOnlyList<string> suppressedPaths, CancellationToken cancellation)
    {
        if (!_canWrite) throw new IOException("当前版本不能保存此安装配置。");
        cancellation.ThrowIfCancellationRequested();
        if (sessionOnly) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Schema, programs, suppressedPaths));
        if (bytes.Length > MaxBytes) throw new IOException("本地安装配置超过容量限制。");
        Directory.CreateDirectory(directory);
        var temporary = _file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(_file)) File.Replace(temporary, _file, _file + (_keepBackup ? ".corrupt" : ".bak"));
            else File.Move(temporary, _file);
            _keepBackup = false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<Document> ReadAsync(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) throw new JsonException();
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (root.TryGetProperty(nameof(Document.SchemaVersion), out var version))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value < 0)
                throw new JsonException();
            if (value > Schema) throw new NotSupportedException();
        }
        return root.Deserialize<Document>() is { Programs: not null } document ? document : throw new JsonException();
    }

    private sealed record Document(int SchemaVersion, IReadOnlyList<ProgramInstallation>? Programs,
        IReadOnlyList<string>? SuppressedPaths);
}

public sealed class LocalProgramEventStore(string directory, bool sessionOnly = false) : IProgramEventStore
{
    private const int Capacity = 2000;
    private const int MaximumBytes = 1024 * 1024;
    private const int Files = 4;
    private readonly string _file = Path.Combine(directory, "bam.log");

    public async Task<IReadOnlyList<ProgramEvent>> LoadAsync(CancellationToken cancellation)
    {
        var result = new Queue<ProgramEvent>();
        for (var index = Files - 1; index >= 0; index--)
        {
            var path = PathAt(index);
            if (!File.Exists(path)) continue;
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-14))
            {
                if (!sessionOnly) File.Delete(path);
                continue;
            }
            if (new FileInfo(path).Length > MaximumBytes + 8192) continue;
            await foreach (var line in File.ReadLinesAsync(path, cancellation).ConfigureAwait(false))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ProgramEvent>(line) is not { Message: not null, Source: not null,
                        Detail: not null } entry) continue;
                    if (result.Count == Capacity) result.Dequeue();
                    result.Enqueue(entry);
                }
                catch (JsonException) { }
            }
        }
        return result.Reverse().ToArray();
    }

    public async Task AppendAsync(ProgramEvent entry, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (sessionOnly) return;
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(entry) + "\n";
        if (File.Exists(_file) && new FileInfo(_file).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
        {
            for (var index = Files - 1; index > 0; index--)
                if (File.Exists(PathAt(index - 1))) File.Move(PathAt(index - 1), PathAt(index), overwrite: true);
        }
        await File.AppendAllTextAsync(_file, line, cancellation).ConfigureAwait(false);
    }

    private string PathAt(int index) => index == 0 ? _file : $"{_file}.{index}";
}
