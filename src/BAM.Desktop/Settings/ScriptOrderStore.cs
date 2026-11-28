using System.IO;
using System.Text.Json;

namespace BAM.Desktop.Settings;

/// <summary>总览卡片的排列顺序。只保存安装 ID，不保存路径、名称或任何本机标识。</summary>
internal sealed class ScriptOrder(ScriptOrderStore store, IReadOnlyList<Guid> ids)
{
    public ScriptOrderStore Store { get; } = store;
    public IReadOnlyList<Guid> Ids { get; set; } = ids;
}

/// <summary>卡片顺序单独成文，避免与主题、托盘等偏好互相覆盖。</summary>
internal sealed class ScriptOrderStore(string? file = null, bool sessionOnly = false)
{
    private const int SchemaVersion = 1;
    private readonly string _file = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BAM", "script-order.json");
    private bool _canWrite = true;
    private Task _pending = Task.CompletedTask;

    public async Task<IReadOnlyList<Guid>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(_file, cancellationToken);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (NotSupportedException)
        {
            _canWrite = false;
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _canWrite = false;
            try
            {
                return await ReadAsync(_file + ".bak", cancellationToken);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return [];
            }
        }
    }

    public Task QueueSave(IReadOnlyList<Guid> order)
    {
        if (sessionOnly || !_canWrite) return Task.CompletedTask;
        return _pending = SaveAfterAsync(_pending, order.ToArray());
    }

    public Task FlushAsync() => _pending;

    private static async Task<IReadOnlyList<Guid>> ReadAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 16384) throw new JsonException();
        var document = await JsonSerializer.DeserializeAsync<ScriptOrderDocument>(stream, cancellationToken: cancellationToken);
        if (document is null || document.SchemaVersion != SchemaVersion) throw new NotSupportedException();
        return document.Order ?? [];
    }

    private async Task SaveAfterAsync(Task previous, Guid[] order)
    {
        await previous.ConfigureAwait(false);
        await Task.Run(async () =>
        {
            var temporary = _file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(stream, new ScriptOrderDocument(SchemaVersion, order),
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);

                if (File.Exists(_file)) File.Replace(temporary, _file, _file + ".bak");
                else File.Move(temporary, _file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 顺序只影响卡片排布，写入失败时保留会话内顺序即可，不必打断用户。
                _canWrite = false;
            }
        }).ConfigureAwait(false);
    }
}

internal sealed record ScriptOrderDocument(int SchemaVersion, IReadOnlyList<Guid>? Order);
